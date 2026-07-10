using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;

namespace IGRF_Interface.Services
{
    public class SerialPortManager
    {
        private SerialPort _port;
        private List<byte> _buffer = new List<byte>();

        // **สำคัญ 1: เพิ่มกุญแจล็อค (Lock Object) เพื่อป้องกันแย่งกันใช้ข้อมูล**
        private readonly object _bufferLock = new object();

        public bool IsSensorReady { get; private set; } = false;
        public event Action<byte[]> OnPacketReceived;
        public bool IsOpen => _port != null && _port.IsOpen;
        private int writeTimeout { get; set; } = 1000;
        private int readTimeout { get; set; } = 1000;
        public bool Connect(string portName, int baudRate = 9600)
        {
            Disconnect();
            try
            {
                _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One);
                _port.Handshake = Handshake.None;
                _port.DtrEnable = true;

                // If packets are fixed-size (7 bytes including CR) we can reduce DataReceived events
                // by waiting until at least one full packet is available.
                _port.ReceivedBytesThreshold = 7;

                _port.WriteTimeout = this.writeTimeout;
                _port.ReadTimeout = this.readTimeout;

                _port.DataReceived += DataReceivedHandler;
                _port.Open();

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                throw new Exception(portName, ex);
            }
        }

        public void Write(byte[] data)
        {
            try
            {
                // ตรวจสอบว่า Port เปิดอยู่จริงไหมก่อนส่ง
                if (IsOpen && data != null && _port.BaseStream.CanWrite)
                {
                    _port.Write(data, 0, data.Length);
                }
            }
            catch (TimeoutException)
            {
                // ถ้าส่งไม่ทัน (Timeout) ให้ข้ามไปเลย ไม่ต้องให้โปรแกรม Error
            }
            catch (Exception) { /* Handle other errors */ }
        }

        public void Disconnect()
        {
            if (_port != null)
            {
                if (_port.IsOpen)
                {
                    _port.DataReceived -= DataReceivedHandler;
                    try { _port.Close(); } catch { }
                }
                _port.Dispose();
                _port = null;
            }

            // ล็อคก่อนเคลียร์ค่า เพื่อไม่ให้ชนกับ DataReceived
            lock (_bufferLock)
            {
                IsSensorReady = false;
                _buffer.Clear();
            }
        }



        private void DataReceivedHandler(object sender, SerialDataReceivedEventArgs e)
        {
            if (_port == null || !_port.IsOpen) return;

            try
            {
                int bytesToRead = _port.BytesToRead;
                if (bytesToRead == 0) return;

                byte[] temp = new byte[bytesToRead];
                _port.Read(temp, 0, bytesToRead);

                // Buffer operations protected by lock; collect complete packets then invoke events outside lock
                List<byte[]> readyPackets = null;
                lock (_bufferLock)
                {
                    _buffer.AddRange(temp);

                    // ป้องกัน Buffer บวมจนกิน Ram (ถ้าข้อมูลเข้ามาผิดๆ เยอะเกินไป)
                    // ล้างทั้งหมดแทนการเก็บ tail ไว้ เพราะ tail ที่ยังไม่ sync framing อาจทำให้ข้อมูลเพี้ยนต่อเนื่อง
                    if (_buffer.Count > 1000) _buffer.Clear();

                    // Logic 1: ตรวจสอบ Handshake "OK"
                    if (!IsSensorReady)
                    {
                        // เช็คว่ามี 0x4F, 0x4B ติดกันไหม
                        for (int i = 0; i < _buffer.Count - 1; i++)
                        {
                            if (_buffer[i] == 0x4F && _buffer[i + 1] == 0x4B)
                            {
                                IsSensorReady = true;
                                _buffer.RemoveRange(0, i + 2); // ลบ OK ออก
                                break;
                            }
                        }
                        // ถ้ายังไม่เจอ OK ก็จบแค่นี้ รอข้อมูลใหม่
                        return;
                    }

                    // Logic 2: ตัด Packet ข้อมูล (Data 6 bytes + CR 1 byte = 7 bytes)
                    // เก็บ packet ที่สมบูรณ์ไว้ในรายการ แล้วเรียก event ข้างนอก lock
                    while (_buffer.Count >= 7)
                    {
                        // ตรวจสอบว่า byte ที่ 7 (index 6) เป็น CR (0x0D หรือ 13) หรือไม่
                        // แต่ data byte เองก็เป็น 0x0D ได้ปกติ ถ้าเช็คแค่ byte เดียวจะ false-positive แล้ว sync เพี้ยนถาวร
                        // ถ้ามีข้อมูลครบ 2 packet (>=14 bytes) ให้เช็คซ้ำว่า byte ที่ 14 (index 13) เป็น CR ของ packet ถัดไปด้วย
                        // ถ้ามีแค่ 7-13 bytes (ยังไม่มี packet ถัดไปให้เช็ค) ก็ยอมรับไปก่อน
                        if (_buffer[6] == 13 && (_buffer.Count < 14 || _buffer[13] == 13))
                        {
                            // ข้อมูลครบถ้วนและถูกต้อง (มี CR ปิดท้ายพอดี)
                            byte[] packet = _buffer.GetRange(0, 7).ToArray();

                            if (readyPackets == null) readyPackets = new List<byte[]>();
                            readyPackets.Add(packet);

                            // ลบข้อมูล 7 bytes ที่ใช้งานแล้วออกจาก Buffer
                            _buffer.RemoveRange(0, 7);
                        }
                        else
                        {
                            // ข้อมูลไม่ตรง (อาจจะเกิดจากข้อมูลขาดหายหรือ out of sync)
                            // ลบออก 1 byte เพื่อทำการ sync ใหม่ในรอบถัดไป
                            _buffer.RemoveAt(0);
                        }
                    }
                }

                // Invoke events outside lock to avoid blocking serial thread if handlers are slow
                if (readyPackets != null)
                {
                    foreach (var pkt in readyPackets)
                    {
                        try { OnPacketReceived?.Invoke(pkt); } catch { }
                    }
                }
            }
            catch (Exception) { /* Handle Error silently */ }
        }
    }
}