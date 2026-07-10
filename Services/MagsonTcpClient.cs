using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace IGRF_Interface.Services
{
    // Client สำหรับอ่านข้อมูลจาก Magson MFG Fluxgate Magnetometer ผ่าน TCP
    // Protocol: Fixed 72-byte binary frame, Little-Endian, ไม่มี sync header
    //   offset 0  : int32 DataType (ค่า valid 1..7, เราสนใจแค่ 1 = TYPE_DAT)
    //   offset 4  : int32 l[3] (l[0]=unix timestamp, l[1]=status word)
    //   offset 16 : float f[14], f[i] อยู่ที่ offset 16+4*i
    //   f[11]=BX2 (offset 60), f[12]=BY2 (offset 64), f[13]=BZ2 (offset 68) หน่วย nT ไม่ต้อง scale
    public class MagsonTcpClient
    {
        private const int FRAME_SIZE = 72;
        private const int TYPE_DAT = 1;
        private const int CONNECT_TIMEOUT_MS = 5000;

        private TcpClient _client;
        private NetworkStream _stream;
        private CancellationTokenSource _cts;
        private Task _readTask;

        private readonly List<byte> _buffer = new List<byte>();
        // **สำคัญ: เพิ่มกุญแจล็อค (Lock Object) เพื่อป้องกันแย่งกันใช้ข้อมูล**
        private readonly object _bufferLock = new object();

        public bool IsOpen { get; private set; } = false;
        public event Action<double, double, double> OnDataReceived;

        public bool Connect(string ip, int port)
        {
            Disconnect();
            try
            {
                _client = new TcpClient();
                Task connectTask = _client.ConnectAsync(ip, port);

                // รอการเชื่อมต่อไม่เกิน 5 วินาที กันค้างถ้า IP/Port ไม่ตอบสนอง
                if (!connectTask.Wait(CONNECT_TIMEOUT_MS) || !_client.Connected)
                {
                    try { _client.Close(); } catch { }
                    _client = null;
                    throw new Exception($"Connect timeout to {ip}:{port}");
                }

                _stream = _client.GetStream();
                IsOpen = true;

                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _readTask = Task.Run(() => ReadLoop(token), token);

                return true;
            }
            catch (Exception ex)
            {
                IsOpen = false;
                Console.WriteLine(ex.ToString());
                throw new Exception($"{ip}:{port}", ex);
            }
        }

        public void Disconnect()
        {
            IsOpen = false;

            try { _cts?.Cancel(); } catch { }

            // ปิด stream/socket ก่อน เพราะ background thread มักค้างอยู่ใน Read() (blocking call)
            // ซึ่งไม่สนใจ CancellationToken -- การปิดตรงนี้จะทำให้ Read() throw exception แล้วจบ loop ทันที
            try { _stream?.Dispose(); } catch { }
            _stream = null;

            try { _client?.Close(); } catch { }
            try { _client?.Dispose(); } catch { }
            _client = null;

            try
            {
                // รอ background thread จบงานสั้นๆ เป็น safety join (ปกติจะจบแทบทันทีหลังปิด stream แล้ว)
                _readTask?.Wait(1000);
            }
            catch { }

            try { _cts?.Dispose(); } catch { }
            _cts = null;
            _readTask = null;

            lock (_bufferLock)
            {
                _buffer.Clear();
            }
        }

        private void ReadLoop(CancellationToken token)
        {
            byte[] temp = new byte[4096];

            try
            {
                while (!token.IsCancellationRequested)
                {
                    NetworkStream stream = _stream;
                    if (stream == null) break;

                    int bytesRead;
                    try
                    {
                        bytesRead = stream.Read(temp, 0, temp.Length);
                    }
                    catch (Exception)
                    {
                        // socket error หรือ stream ถูกปิด -> จบ loop เงียบๆ
                        break;
                    }

                    if (bytesRead <= 0) break; // ฝั่งตรงข้ามปิดการเชื่อมต่อ

                    // Buffer operations protected by lock; เก็บ frame ที่ parse เสร็จแล้วไว้เรียก event ภายนอก lock
                    List<(double bx, double by, double bz)> readyFrames = null;

                    lock (_bufferLock)
                    {
                        for (int i = 0; i < bytesRead; i++) _buffer.Add(temp[i]);

                        // ป้องกัน Buffer บวมจนกิน Ram (ถ้าข้อมูลเข้ามาผิดๆ เยอะเกินไป)
                        if (_buffer.Count > 4096) _buffer.Clear();

                        // ไม่มี sync header ใน protocol นี้ -> ต้อง resync ทีละ byte เมื่อ DataType ไม่ valid (1..7)
                        while (_buffer.Count >= FRAME_SIZE)
                        {
                            int dataType = BitConverter.ToInt32(_buffer.GetRange(0, 4).ToArray(), 0);

                            if (dataType < 1 || dataType > 7)
                            {
                                // DataType ไม่ valid -> ตัดออก 1 byte แล้วลอง sync ใหม่ในรอบถัดไป
                                _buffer.RemoveAt(0);
                                continue;
                            }

                            byte[] frame = _buffer.GetRange(0, FRAME_SIZE).ToArray();
                            _buffer.RemoveRange(0, FRAME_SIZE);

                            if (dataType == TYPE_DAT)
                            {
                                // f[11]=BX2 (offset 60), f[12]=BY2 (offset 64), f[13]=BZ2 (offset 68)
                                double bx2 = BitConverter.ToSingle(frame, 60);
                                double by2 = BitConverter.ToSingle(frame, 64);
                                double bz2 = BitConverter.ToSingle(frame, 68);

                                if (readyFrames == null) readyFrames = new List<(double, double, double)>();
                                readyFrames.Add((bx2, by2, bz2));
                            }
                            // DataType อื่นๆ (2..7) แค่ consume 72 bytes ทิ้งไป ไม่ต้องทำอะไรเพิ่ม
                        }
                    }

                    // Invoke events นอก lock กันบล็อค read thread ถ้า handler ทำงานช้า
                    if (readyFrames != null)
                    {
                        foreach (var f in readyFrames)
                        {
                            try { OnDataReceived?.Invoke(f.bx, f.by, f.bz); } catch { }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // กัน exception หลุดออกจาก background thread
            }
            finally
            {
                IsOpen = false;
            }
        }
    }
}
