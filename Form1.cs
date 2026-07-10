using Geo;
using Geo.Geodesy;
using Geo.Geomagnetism;
using Geo.Geomagnetism.Models;
using IGRF_Interface.Models;
// Custom Services (Namespace ต้องตรงกับที่คุณสร้าง)
using IGRF_Interface.Services;
using IGRF_Interface.Utils;
// Third-party Libraries
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.WindowsForms;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IGRF_Interface_Demo1._1
{
    public partial class Form1 : Form
    {
        // ==========================================
        // 1. Constants & Configuration
        // ==========================================
        private const int CONTROLLER_BAUD_RATE = 9600;
        private const byte HEADER_BYTE = 0xA0;
        private const int SENDER_INTERVAL = 100;
        private const int UI_REFRESH_RATE_MS = 50;
        private const bool ENABLE_RX_LOG = false;

        // ==========================================
        // 2. Services & Managers
        // ==========================================
        private readonly SerialPortManager _sensorManager = new SerialPortManager();
        private readonly SensorService _sensorService = new SensorService();
        private readonly SatelliteService _satService = new SatelliteService();
        private readonly CalculationService _calcService = new CalculationService();

        private AppConfig _appConfig = new AppConfig();
        private string ConfigFilePath => Path.Combine(Application.StartupPath, "SystemConfig.json");

        private GraphManager _graphX, _graphY, _graphZ;
        private readonly PidController _pidX = new PidController();
        private readonly PidController _pidY = new PidController();
        private readonly PidController _pidZ = new PidController();

        private GeomagnetismCalculator _geomagnetismCalculator;

        // ==========================================
        // 3. Hardware & Data Variables
        // ==========================================
        private SerialPort _controllerPort = new SerialPort();

        // Data Variables
        private double _magX_nT, _magY_nT, _magZ_nT;
        private double _RawmagX_nT, _RawmagY_nT, _RawmagZ_nT;
        private double _setpointX, _setpointY, _setpointZ;
        private double _outputX, _outputY, _outputZ;
        private double[,] _intensityResults; // สำหรับ Map

        // Visualization & Simulation
        private LineSeries _seriesSatTrack;
        private DateTime _lastUiUpdate = DateTime.MinValue;
        private double _simTimeOffset = 0;

        // Timers
        private Timer _timerPidX = new Timer { Interval = 100 };
        private Timer _timerPidY = new Timer { Interval = 100 };
        private Timer _timerPidZ = new Timer { Interval = 100 };

        // Logging
        private bool _isLogging = false;
        private int _logCount = 0;
        private string _logFileName = "";
        private StreamWriter _logWriter;
        private DateTime _logDate;
        private string _logBaseName;
        private double _errX, _errY, _errZ, _errPerX, _errPerY, _errPerZ;

        // Sensor watchdog / auto-reconnect
        private DateTime _lastPacketTime = DateTime.Now;
        private string _sensorPortName;
        private bool _sensorIntended;
        private System.Windows.Forms.Timer _watchdogTimer;
        private bool _reconnecting;

        // Controller auto-reconnect
        private int _ctrlWriteFails;
        private bool _controllerIntended;
        private string _controllerPortName;

        public Form1()
        {
            InitializeComponent();
            InitializeSystem();

            _watchdogTimer = new System.Windows.Forms.Timer { Interval = 10000 };
            _watchdogTimer.Tick += WatchdogTimer_Tick;
            _watchdogTimer.Start();
        }

        private void InitializeSystem()
        {
            try
            {
                // Init Math Models
                _geomagnetismCalculator = new GeomagnetismCalculator(Spheroid.Wgs84, new List<IGeomagneticModel> { new Wmm2025() });

                // Init Graphs
                _graphX = new GraphManager(plotViewX, "PID X-Axis");
                _graphY = new GraphManager(plotViewY, "PID Y-Axis");
                _graphZ = new GraphManager(plotViewZ, "PID Z-Axis");

                // Init Events
                _sensorManager.OnPacketReceived += HandleSensorPacket;

                timerSender.Interval = SENDER_INTERVAL;
                timerSender.Tick += timerSender_Tick;

                // Bind PID Logic (Return value to Output Variable)
                // แกน X
                _timerPidX.Tick += (s, e) =>
                {
                    try
                    {
                        // คำนวณ PID และรับค่ากลับมาใส่ _outputX
                        _outputX = RunPidLogic(_pidX, _setpointX, _magX_nT, textSysX2, _graphX);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("PID X Error: " + ex.Message);
                        _timerPidX.Stop();
                    }
                };

                // แกน Y
                _timerPidY.Tick += (s, e) =>
                {
                    try {
                        _outputY = RunPidLogic(_pidY, _setpointY, _magY_nT, textSysY2, _graphY); }
                    catch (Exception ex)
                    {
                        Console.WriteLine("PID Y Error: " + ex.Message);
                        _timerPidY.Stop();
                    }
                };

                // แกน Z
                _timerPidZ.Tick += (s, e) =>
                {
                    try { _outputZ = RunPidLogic(_pidZ, _setpointZ, _magZ_nT, textSysZ2, _graphZ); }
                    catch (Exception ex)
                    {
                        Console.WriteLine("PID Z Error: " + ex.Message);
                        _timerPidZ.Stop();
                    }
                };

                // Load Config
                LoadSystemConfig();

                // Set Button Defaults
                UpdateButtonState(ConnectSensor, false); // สมมติปุ่มชื่อ ConnectSensor
                UpdateButtonState(button1, false); // สมมติปุ่มชื่อ buttonConnectController (แก้ให้ตรงชื่อจริง)
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Startup Error: {ex.Message}");
            }
        }
        private void InitializeSatellitePresets()
        {
            cboSatelliteList.Items.Clear();
            cboSatelliteList.Items.Add(new SatelliteInfo { Name = "-- Manual --" });
            cboSatelliteList.Items.Add(new SatelliteInfo { Name = "ISS (ZARYA)", Line1 = "1 25544U 98067A   26036.50214262  .00012860  00000+0  24571-3 0  9997", Line2 = "2 25544  51.6316 231.4727 0011155  67.3664 292.8503 15.48414003551342" });
            cboSatelliteList.Items.Add(new SatelliteInfo { Name = "THEOS", Line1 = "1 33396U 08049A   26040.79524366  .00000115  00000+0  73973-4 0  9998", Line2 = "2 33396  98.5761 102.1159 0001093  84.4399 275.6902 14.20111503899882" });
            cboSatelliteList.Items.Add(new SatelliteInfo { Name = "STARLINK-31229", Line1 = "1 58986U 24031X   26036.39436483  .00000189  00000+0  18520-4 0  9990", Line2 = "2 58986  53.1592  87.0519 0001084  99.7418 260.3705 15.30187592111562" });
            cboSatelliteList.Items.Add(new SatelliteInfo { Name = "BEIDOU-3 M1", Line1 = "1 43001U 17069A   26036.57903495 -.00000046  00000+0  00000+0 0  9999", Line2 = "2 43001  56.5755  67.4198 0011656 306.0537  53.8974  1.86231175 56158" });
            cboSatelliteList.Items.Add(new SatelliteInfo { Name = "GPS BIIF-3  (PRN 24)", Line1 = "1 38833U 12053A   26035.19833436  .00000007  00000+0  00000+0 0  9994", Line2 = "2 38833  53.5640 149.0493 0176647  64.5822 297.2173  2.00565464 96770" });
            cboSatelliteList.Items.Add(new SatelliteInfo { Name = "COSMOS 2564 (761)", Line1 = "1 54377U 22161A   26036.37904223  .00000053  00000+0  00000+0 0  9991", Line2 = "2 54377  64.7420 197.0834 0008836 205.0145 154.9145  2.13102005 24824" });
            cboSatelliteList.Items.Add(new SatelliteInfo { Name = "Fengyun (4A)", Line1 = "1 41882U 16077A   26036.93722848 -.00000358  00000+0  00000+0 0  9999", Line2 = "2 41882   1.9889  81.7930 0006361 133.3026  22.0577  1.00276422 33612" });
            cboSatelliteList.Items.Add(new SatelliteInfo { Name = "Thaicom 6", Line1 = "1 39500U 14002A   26036.72819522 -.00000122  00000+0  00000+0 0  9991", Line2 = "2 43001  56.5755  67.4198 0011656 306.0537  53.8974  1.86231175 56158" });
            cboSatelliteList.Items.Add(new SatelliteInfo { Name = "METEOSAT-11 (MSG-4)", Line1 = "1 40732U 15034A   26036.91460836  .00000062  00000+0  00000+0 0  9999", Line2 = "2 40732   2.8710  71.8338 0001172 241.7640 161.3542  1.00267859  5905" });
            cboSatelliteList.SelectedIndex = 0;

            // Add Event Handler manually
            cboSatelliteList.SelectedIndexChanged += cboSatelliteList_SelectedIndexChanged_1;
        }
     

        private void UpdateErrorStyle(TextBox tb)
        {
            if (double.TryParse(tb.Text, out double val))
            {
                if (Math.Abs(val) > 5.0) { tb.BackColor = Color.Red; tb.ForeColor = Color.White; }
                else { tb.BackColor = Color.LightGreen; tb.ForeColor = Color.Black; }
            }
        }

        private void ValidateDoubleInput(TextBox tb)
        {
            tb.BackColor = double.TryParse(tb.Text, out _) ? Color.White : Color.LightPink;
        }
        private void Form1_Load(object sender, EventArgs e)
        {
            UpdatePortList();
            InitializeSatellitePresets();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Stop timers & Disconnect
            _timerPidX.Stop(); _timerPidY.Stop(); _timerPidZ.Stop();
            SatPosTimer.Stop(); timerSender.Stop(); timer_sensor.Stop();
            _watchdogTimer?.Stop();

            _sensorManager?.Disconnect();
            if (_controllerPort != null && _controllerPort.IsOpen)
            {
                try { _controllerPort.Close(); } catch { }
                _controllerPort.Dispose();
            }

            lock (_fileLock) { _logWriter?.Dispose(); _logWriter = null; }
            base.OnFormClosing(e);
        }

        // ==========================================
        // 4. Sensor Data Handling (High Frequency)
        // ==========================================
        private void HandleSensorPacket(byte[] packet)
        {
            _lastPacketTime = DateTime.Now;

            // 1. Convert Packet to Raw Data
            var rawData = _sensorService.ProcessData(packet);
            _RawmagX_nT = _sensorService.LastRawX;
            _RawmagY_nT = _sensorService.LastRawY;
            _RawmagZ_nT = _sensorService.LastRawZ;
            // 2. Process Data (Filter & Calculate Error) using Service

            var processed = _calcService.ProcessSensorData(rawData, _setpointX, _setpointY, _setpointZ);

            // Update Global Variables for Logging/PID
            _magX_nT = processed.MagX;
            _magY_nT = processed.MagY;
            _magZ_nT = processed.MagZ;
            _errX = processed.ErrorX; _errY = processed.ErrorY; _errZ = processed.ErrorZ;
            _errPerX = processed.ErrorPerX; _errPerY = processed.ErrorPerY; _errPerZ = processed.ErrorPerZ;

            // 5. Log Data (เก็บทุก packet ไม่ผูกกับ throttle UI ด้านล่าง)
            SaveLogData();

            // 3. UI Throttling
            if ((DateTime.Now - _lastUiUpdate).TotalMilliseconds < UI_REFRESH_RATE_MS) return;
            _lastUiUpdate = DateTime.Now;

            // 4. Update UI
            if (this.IsHandleCreated && !this.IsDisposed)
            {
                string rxLine = $"{DateTime.Now:HH:mm:ss.fff} RX: {BitConverter.ToString(packet)}";

                this.BeginInvoke(new Action(() =>
                {
                    textSensorX2.Text = processed.MagX.ToString("F2");
                    textSensorY2.Text = processed.MagY.ToString("F2");
                    textSensorZ2.Text = processed.MagZ.ToString("F2");

                    textBoxErrorX.Text = processed.ErrorX.ToString("F2");
                    textBoxErrorY.Text = processed.ErrorY.ToString("F2");
                    textBoxErrorZ.Text = processed.ErrorZ.ToString("F2");

                    textBoxErrorX_per.Text = processed.ErrorPerX.ToString("F2");
                    textBoxErrorY_per.Text = processed.ErrorPerY.ToString("F2");
                    textBoxErrorZ_per.Text = processed.ErrorPerZ.ToString("F2");

                    Count_label.Text = _logCount.ToString();
                    debug_label_rx.Text = rxLine;

                    // Graph update handled by PID logic if running, or manually here if stopped (optional)
                    if (!_timerPidX.Enabled) _graphX.Update(_setpointX, processed.MagX);
                    if (!_timerPidY.Enabled) _graphY.Update(_setpointY, processed.MagY);
                    if (!_timerPidZ.Enabled) _graphZ.Update(_setpointZ, processed.MagZ);

                }));
            }

            // Console + ไฟล์ rx.log เปิดเฉพาะตอน debug (ปิดไว้ default กัน I/O ทุก packet บน serial thread)
            if (ENABLE_RX_LOG)
            {
                try
                {
                    string rxLine = $"{DateTime.Now:HH:mm:ss.fff} RX: {BitConverter.ToString(packet)}";
                    Console.WriteLine(rxLine);
                    File.AppendAllText(Path.Combine(Application.StartupPath, "rx.log"), rxLine + Environment.NewLine);
                }
                catch { }
            }
        }

        private void OpenLogWriter()
        {
            string logsDir = Path.Combine(Application.StartupPath, "logs");
            Directory.CreateDirectory(logsDir);
            _logFileName = Path.Combine(logsDir, $"{_logBaseName}_{DateTime.Now:yyyy-MM-dd}.csv");

            bool isNew = !File.Exists(_logFileName) || new FileInfo(_logFileName).Length == 0;
            _logWriter = new StreamWriter(_logFileName, append: true) { AutoFlush = true };
            if (isNew) _logWriter.Write("Timestamp,MagX,MagY,MagZ,SetX,SetY,SetZ,ErrX,ErrY,ErrZ,OutX,OutY,OutZ,KpX,KiX,KdX,KpY,KiY,KdY,KpZ,KiZ,KdZ\n");
            _logDate = DateTime.Today;
        }

        private void SaveLogData()
        {
            if (!_isLogging) return;

            try
            {
                string timeStamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string dataLine = $"{timeStamp},{_magX_nT:F2},{_magY_nT:F2},{_magZ_nT:F2}," +
                                  $"{_setpointX:F2},{_setpointY:F2},{_setpointZ:F2}," +
                                  $"{_errX:F2},{_errY:F2},{_errZ:F2}," +
                                  $"{_outputX:F2},{_outputY:F2},{_outputZ:F2}," +
                                  $"{_pidX.Kp:F3},{_pidX.Ki:F3},{_pidX.Kd:F3}," +
                                  $"{_pidY.Kp:F3},{_pidY.Ki:F3},{_pidY.Kd:F3}," +
                                  $"{_pidZ.Kp:F3},{_pidZ.Ki:F3},{_pidZ.Kd:F3}\n";

                lock (_fileLock)
                {
                    if (DateTime.Today != _logDate)
                    {
                        _logWriter?.Dispose();
                        OpenLogWriter();
                    }
                    _logWriter?.Write(dataLine);
                }
                _logCount++;
            }
            catch { }
        }

        // ==========================================
        // 5. Connection Management (Async + Toggle)
        // ==========================================
        private void UpdateButtonState(Button btn, bool isConnected)
        {
            if (btn == null) return;
            if (isConnected)
            {
                btn.Text = "⏹ Disconnect";
                btn.BackColor = Color.Salmon;
                btn.ForeColor = Color.White;
            }
            else
            {
                btn.Text = "▶ Connect";
                btn.BackColor = Color.LightGreen;
                btn.ForeColor = Color.Black;
            }
            btn.Enabled = true;
        }

        private async void ConnectSensor_Click(object sender, EventArgs e)
        {
            Button btn = sender as Button; // ใช้ปุ่มที่กดมา

            // 1. ถ้าต่ออยู่แล้ว -> Disconnect
            if (_sensorManager.IsOpen)
            {
                try
                {
                    btn.Enabled = false;
                    timer_sensor.Stop();
                    _sensorIntended = false;
                    await Task.Run(() => _sensorManager.Disconnect());
                    UpdateButtonState(btn, false);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Disconnect Error: {ex.Message}");
                    btn.Enabled = true;
                }
                return;
            }

            // 2. ถ้ายังไม่ต่อ -> Connect
            try
            {
                string portName = ExtractPortName(cboSensorPort.Text);
                if (string.IsNullOrEmpty(portName))
                {
                    MessageBox.Show("Please select a port.");
                    return;
                }

                btn.Enabled = false;
                btn.Text = "Connecting...";
                btn.BackColor = Color.LightYellow;

                await Task.Run(() => _sensorManager.Connect(portName));

                timer_sensor.Start();
                _sensorPortName = portName;
                _sensorIntended = true;
                _lastPacketTime = DateTime.Now;
                UpdateButtonState(btn, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Connection Failed: {ex.Message}");
                UpdateButtonState(btn, false);
            }
        }

        private async void WatchdogTimer_Tick(object sender, EventArgs e)
        {
            if (!_sensorIntended || _reconnecting) return;
            if ((DateTime.Now - _lastPacketTime).TotalSeconds <= 15) return;

            _reconnecting = true;
            try
            {
                debug_label_rx.Text = "SENSOR LOST - RECONNECTING...";
                await Task.Run(() =>
                {
                    _sensorManager.Disconnect();
                    _sensorManager.Connect(_sensorPortName);
                });
                if (!_sensorIntended)
                {
                    _sensorManager.Disconnect();
                    return;
                }
                timer_sensor.Start();
                _lastPacketTime = DateTime.Now;
            }
            catch
            {
                // ล้มเหลวก็ปล่อยไว้ รอบถัดไปจะลองใหม่เอง (ห้าม popup ค้างตอนไม่มีคนเฝ้า)
            }
            finally
            {
                _reconnecting = false;
            }
        }

        private async void ConnectController_Click(object sender, EventArgs e)
        {
            Button btn = sender as Button;

            // 1. Disconnect
            if (_controllerPort.IsOpen)
            {
                try
                {
                    timerSender.Stop(); // หยุดส่ง
                    _controllerIntended = false;
                    _controllerPort.Close();
                    UpdateButtonState(btn, false);
                    MessageBox.Show("Controller Disconnected.");
                }
                catch (Exception ex) { MessageBox.Show($"Error: {ex.Message}"); }
                return;
            }

            // 2. Connect
            try
            {
                string portName = ExtractPortName(cboControllerPort.Text);
                if (string.IsNullOrEmpty(portName))
                {
                    MessageBox.Show("Please select a port.");
                    return;
                }

                btn.Enabled = false;
                btn.Text = "Connecting...";

                await Task.Run(() =>
                {
                    _controllerPort.PortName = portName;
                    _controllerPort.BaudRate = CONTROLLER_BAUD_RATE;
                    _controllerPort.Parity = Parity.None;
                    _controllerPort.StopBits = StopBits.One;
                    _controllerPort.Handshake = Handshake.None;
                    _controllerPort.DataBits = 8;
                    _controllerPort.DtrEnable = true;
                    _controllerPort.Open();
                });

                // Note: ยังไม่สั่ง timerSender.Start() ตรงนี้ (รอปุ่ม Start PID)
                _controllerIntended = true;
                _controllerPortName = portName;
                _ctrlWriteFails = 0;
                UpdateButtonState(btn, true);
                MessageBox.Show($"Controller Connected: {portName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Controller Error: {ex.Message}");
                UpdateButtonState(btn, false);
            }
        }

        private async void UpdatePortList()
        {
            cboSensorPort.Items.Clear();
            cboControllerPort.Items.Clear();
            btnRefreshPorts.Enabled = false;

            try
            {
                var ports = await Task.Run(() =>
                {
                    var list = new List<string>();
                    try
                    {
                        using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE Caption like '%(COM%'"))
                        {
                            foreach (var item in searcher.Get()) list.Add(item["Caption"]?.ToString() ?? "");
                        }
                    }
                    catch { /* WMI fallback */ }

                    if (list.Count == 0) list.AddRange(SerialPort.GetPortNames());
                    return list.Distinct().OrderBy(x => x).ToList();
                });

                foreach (var p in ports)
                {
                    cboSensorPort.Items.Add(p);
                    cboControllerPort.Items.Add(p);
                }

                if (cboSensorPort.Items.Count > 0) cboSensorPort.SelectedIndex = 0;
                if (cboControllerPort.Items.Count > 0) cboControllerPort.SelectedIndex = 0;
            }
            finally { btnRefreshPorts.Enabled = true; }
        }

        // ==========================================
        // 6. Data Transmission (Optimized + CRC)
        // ==========================================
        private void timerSender_Tick(object sender, EventArgs e)
        {
            if (_controllerPort == null || !_controllerPort.IsOpen) return;

            byte[] packet = new byte[15];
            packet[0] = HEADER_BYTE; // 0xA0

            Buffer.BlockCopy(BitConverter.GetBytes((float)_outputX), 0, packet, 1, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((float)_outputY), 0, packet, 5, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((float)_outputZ), 0, packet, 9, 4);

            // Debug ดูค่าก่อนส่ง
            // Console.WriteLine($"TX: {BitConverter.ToString(packet)}");

            // CRC Calculation
            byte[] payloadForCrc = new byte[13];
            Array.Copy(packet, 0, payloadForCrc, 0, 13);
            byte[] crc = CrcUtils.CalculateModRTU_CRC(payloadForCrc);

            packet[13] = crc[0];
            packet[14] = crc[1];

            // Debug ดูค่าพร้อม CRC (prepare hex once)
            string hex = BitConverter.ToString(packet);

            // Update UI safely (timer is UI thread but keep safe for future changes)
            if (debug_label_x != null && !debug_label_x.IsDisposed)
            {
                if (debug_label_x.InvokeRequired)
                    debug_label_x.BeginInvoke(new Action(() => debug_label_x.Text = "TX: " + hex));
                else
                    debug_label_x.Text = "TX: " + hex;
            }

            try
            {
                _controllerPort.Write(packet, 0, packet.Length);
                _ctrlWriteFails = 0;
            }
            catch (Exception ex)
            {
                // Don't crash UI on serial write issues — log for diagnostics
                try { Console.WriteLine("TX Write Error: " + ex.Message); } catch { }
                _ctrlWriteFails++;

                if (_ctrlWriteFails >= 10 && _controllerIntended)
                {
                    try
                    {
                        _controllerPort.Close();
                        _controllerPort.PortName = _controllerPortName;
                        _controllerPort.Open();
                        _ctrlWriteFails = 0;
                    }
                    catch { _ctrlWriteFails = 0; }
                }
            }
        }

        // ==========================================
        // 7. Satellite & Map Logic (Async Loading)
        // ==========================================
        private async void btnLoadMapData_Click(object sender, EventArgs e)
        {
            Button btn = sender as Button;

            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
                ofd.Title = "Select Geomagnetic Grid Data";

                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    // ล็อกปุ่มเพื่อป้องกันการกดซ้ำ
                    if (btn != null)
                    {
                        btn.Enabled = false;
                        btn.Text = "Processing...";
                    }

                    try
                    {
                        // =========================================================
                        // ส่วนที่ 1: คำนวณเบื้องหลัง (Background Thread)
                        // =========================================================
                        await Task.Run(() =>
                        {
                            string[] lines = File.ReadAllLines(ofd.FileName);
                            int fullRows = lines.Length;

                            // [Tuning] ลดความละเอียดลงเล็กน้อยเพื่อความเร็ว (สำคัญมากสำหรับ Contour)
                            // step = 1 (ละเอียดสุด, ช้า), step = 2 (เร็วขึ้น 4 เท่า), step = 3 (เร็วมาก)
                            int step = 2;

                            int newRows = 180 / step;
                            int newCols = 360 / step;

                            // สร้าง Array รอไว้
                            var intensityData = new double[newCols, newRows];
                            var latData = new double[newRows];
                            var lonData = new double[newCols];

                            // ใช้ Parallel Loop เพื่อความเร็วในการแปลง String -> Double
                            Parallel.For(0, newRows, i =>
                            {
                                int originalLatIndex = i * step;
                                if (originalLatIndex >= fullRows) return;

                                string line = lines[originalLatIndex];
                                var parts = line.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                                for (int j = 0; j < newCols; j++)
                                {
                                    int originalLonIndex = j * step;
                                    if (originalLonIndex >= parts.Length) break;

                                    if (double.TryParse(parts[originalLonIndex], out double val))
                                    {
                                        intensityData[j, i] = val;
                                    }

                                    // สร้างแกน X (ทำซ้ำหน่อยไม่เป็นไร เพราะ Parallel เร็วมาก)
                                    lonData[j] = -180 + originalLonIndex;
                                }
                                // สร้างแกน Y
                                latData[i] = -90 + originalLatIndex;
                            });

                            // ส่งค่ากลับไปที่ตัวแปร Global (เฉพาะ Data)
                            // ส่วนแกน lats/lons เราจะไปสร้างใหม่หน้างาน หรือส่งผ่านตัวแปรอื่นก็ได้
                            // เพื่อความง่าย ในที่นี้เราจะสร้างแกนใหม่อีกรอบใน UI Thread
                            this._intensityResults = intensityData;
                        });

                        // =========================================================
                        // ส่วนที่ 2: วาดกราฟ (UI Thread)
                        // =========================================================
                        if (btn != null) btn.Text = "Drawing...";

                        if (plotViewInt2.Model == null)
                        {
                            plotViewInt2.Model = new PlotModel { Title = "Geomagnetic Field Map" };
                            plotViewInt2.Model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Longitude" });
                            plotViewInt2.Model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Latitude" });
                        }

                        // 1. เคลียร์แผนที่เก่า (ลบทั้ง Contour และ HeatMap เผื่อมี)
                        var oldMaps = plotViewInt2.Model.Series
                            .Where(s => s is ContourSeries || s is HeatMapSeries).ToList();
                        foreach (var map in oldMaps) plotViewInt2.Model.Series.Remove(map);

                        // 2. ลบแกนสีเก่าออก (ถ้ามี)
                        var oldAxes = plotViewInt2.Model.Axes.OfType<LinearColorAxis>().ToList();
                        foreach (var axis in oldAxes) plotViewInt2.Model.Axes.Remove(axis);

                        // 3. สร้างแกนข้อมูลใหม่ ให้ตรงกับ Step ที่ลดลง
                        int drawStep = 2; // ต้องตรงกับข้างบน
                        int drawRows = 180 / drawStep;
                        int drawCols = 360 / drawStep;
                        double[] lats = new double[drawRows];
                        double[] lons = new double[drawCols];
                        for (int i = 0; i < drawRows; i++) lats[i] = -90 + (i * drawStep);
                        for (int j = 0; j < drawCols; j++) lons[j] = -180 + (j * drawStep);

                        // 4. สร้าง ContourSeries
                        var cs = new ContourSeries
                        {
                            Title = "Intensity",
                            ColumnCoordinates = lons,
                            RowCoordinates = lats,
                            Data = _intensityResults,

                            // [Config] ตั้งค่าเส้นให้สวยและไม่รก
                            ContourLevelStep = 2000,   // วาดเส้นทุกๆ 2000 nT (ถ้าเลขน้อยเส้นจะถี่ยิบจนค้าง)
                            LabelStep = 2,             // เขียนตัวเลขกำกับทุกๆ 2 เส้น
                            StrokeThickness = 1.0,     // ความหนาเส้น
                            LineStyle = LineStyle.Solid,
                            Color = OxyColors.Automatic // ให้ OxyPlot สุ่มสีเส้นเอง หรือใช้ OxyColors.Black
                        };

                        // 5. แทรกไว้ล่างสุด (Index 0)
                        plotViewInt2.Model.Series.Insert(0, cs);

                        // 6. เช็คว่ามีจุดดาวเทียมไหม ถ้าหายไปให้เติมกลับ
                        if (_seriesSatTrack != null && !plotViewInt2.Model.Series.Contains(_seriesSatTrack))
                        {
                            plotViewInt2.Model.Series.Add(_seriesSatTrack);
                        }

                        plotViewInt2.Model.InvalidatePlot(true);
                        MessageBox.Show("Map Loaded Successfully!");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Error: " + ex.Message);
                    }
                    finally
                    {
                        if (btn != null)
                        {
                            btn.Text = "Load Map Data";
                            btn.Enabled = true;
                        }
                    }
                }
            }
        }

        private void SatPosTimer_Tick(object sender, EventArgs e)
        {
            if (TimeSpeed.Value != 0) _simTimeOffset += TimeSpeed.Value;
            var currentTime = DateTime.UtcNow.AddSeconds(_simTimeOffset);

            if (TimeSim_label != null) TimeSim_label.Text = currentTime.ToString("yyyy-MM-dd HH:mm:ss");

            var satPos = _satService.CalculatePosition(currentTime);

            if (_seriesSatTrack != null && plotViewInt2.Model != null)
            {
                _seriesSatTrack.Points.Clear();
                _seriesSatTrack.Points.Add(new DataPoint(satPos.Lon, satPos.Lat));
                plotViewInt2.Model.InvalidatePlot(true);
            }

            tbxSatLat.Text = satPos.Lat.ToString("F3");
            tbxSatLon.Text = satPos.Lon.ToString("F3");
            tbxSatHeight.Text = satPos.Alt.ToString("F3");
            tbxX.Text = satPos.X.ToString("F3");
            tbxY.Text = satPos.Y.ToString("F3");
            tbxZ.Text = satPos.Z.ToString("F3");

            //var mag = _geomagnetismCalculator.TryCalculate(new Geo.Coordinate(satPos.Lat, satPos.Lon), currentTime);
            var mag = _geomagnetismCalculator.TryCalculate(new Geo.Coordinate(satPos.Lat, satPos.Lon), satPos.Alt, currentTime);
            _setpointX = mag.X; _setpointY = mag.Y; _setpointZ = mag.Z;

            textSetpointXMag.Text = _setpointX.ToString("F3");
            textSetpointYMag.Text = _setpointY.ToString("F3");
            textSetpointZMag.Text = _setpointZ.ToString("F3");
            tbxSatInt.Text = mag.TotalIntensity.ToString("F3");
        }

        // ==========================================
        // 8. PID Controls & Start Logic
        // ==========================================
        private double RunPidLogic(PidController pid, double sp, double pv, Control display, GraphManager graph)
        {
            double result = pid.Calculate(sp, pv);
            if (display.InvokeRequired) display.BeginInvoke(new Action(() => display.Text = result.ToString("F2")));
            else display.Text = result.ToString("F2");
            graph.Update(sp, pv);
            return result;
        }

        private void UpdatePidParams(PidController pid, string kp, string ki, string kd)
        {
            if (double.TryParse(kp, out double p)) pid.Kp = p;
            if (double.TryParse(ki, out double i)) pid.Ki = i;
            if (double.TryParse(kd, out double d)) pid.Kd = d;
        }

        // [Logic: Start PID + Start Sending Data]
        private void StartX_Click(object sender, EventArgs e)
        {
            // แปลง sender เป็นปุ่ม เพื่อให้เปลี่ยนสี/ข้อความได้
            Button btn = sender as Button;

            // --- กรณีที่ 1: กำลังทำงานอยู่ -> สั่งหยุด (PAUSE) ---
            if (_timerPidX.Enabled)
            {
                _timerPidX.Stop();

                // ถ้าต้องการให้ Pause แล้ว Reset ค่า PID ด้วย (เริ่มใหม่หมด) ให้เปิดบรรทัดนี้:
                // _pidX.Reset(); 
                // _outputX = 0; // อาจจะเคลียร์ค่า Output ด้วยถ้าต้องการ

                // เปลี่ยนหน้าตาปุ่มกลับเป็น "Start"
                if (btn != null)
                {
                    btn.Text = "Start PID X";
                    btn.BackColor = Color.LightGreen; // สีเขียว = พร้อมเริ่ม
                }
            }
            // --- กรณีที่ 2: หยุดอยู่ -> สั่งเริ่ม (START) ---
            else
            {
                // 1. อัปเดตค่า PID
                UpdatePidParams(_pidX, KpX.Text, KiX.Text, KdX.Text);

                // 2. เริ่มคำนวณ
                _timerPidX.Start();

                // 3. เริ่มส่งข้อมูล (ถ้ายังไม่ส่ง)
                if (!timerSender.Enabled && _controllerPort.IsOpen)
                {
                    timerSender.Start();
                }

                // เปลี่ยนหน้าตาปุ่มเป็น "Stop/Pause"
                if (btn != null)
                {
                    btn.Text = "Pause PID X";
                    btn.BackColor = Color.Salmon; // สีแดง/ส้ม = กำลังทำงาน
                }
            }
        }
       
        private void TuningXkp_Click(object sender, EventArgs e) => UpdatePidParams(_pidX, KpX.Text, KiX.Text, KdX.Text);
        private void ResetKpX_Click(object sender, EventArgs e) => KpX.Text = "0";

        private void StartY_Click(object sender, EventArgs e)
        {
            // แปลง sender เป็นปุ่ม เพื่อให้เปลี่ยนสี/ข้อความได้
            Button btn = sender as Button;

            // --- กรณีที่ 1: กำลังทำงานอยู่ -> สั่งหยุด (PAUSE) ---
            if (_timerPidY.Enabled)
            {
                _timerPidY.Stop();

                // ถ้าต้องการให้ Pause แล้ว Reset ค่า PID ด้วย (เริ่มใหม่หมด) ให้เปิดบรรทัดนี้:
                // _pidX.Reset(); 
                // _outputX = 0; // อาจจะเคลียร์ค่า Output ด้วยถ้าต้องการ

                // เปลี่ยนหน้าตาปุ่มกลับเป็น "Start"
                if (btn != null)
                {
                    btn.Text = "Start PID Y";
                    btn.BackColor = Color.LightGreen; // สีเขียว = พร้อมเริ่ม
                }
            }
            // --- กรณีที่ 2: หยุดอยู่ -> สั่งเริ่ม (START) ---
            else
            {
                // 1. อัปเดตค่า PID
                UpdatePidParams(_pidY, KpX.Text, KiX.Text, KdX.Text);

                // 2. เริ่มคำนวณ
                _timerPidY.Start();

                // 3. เริ่มส่งข้อมูล (ถ้ายังไม่ส่ง)
                if (!timerSender.Enabled && _controllerPort.IsOpen)
                {
                    timerSender.Start();
                }

                // เปลี่ยนหน้าตาปุ่มเป็น "Stop/Pause"
                if (btn != null)
                {
                    btn.Text = "Pause PID Y";
                    btn.BackColor = Color.Salmon; // สีแดง/ส้ม = กำลังทำงาน
                }
            }
        }
        private void TuningYkp_Click(object sender, EventArgs e) => UpdatePidParams(_pidY, KpY.Text, KiY.Text, KdY.Text);
        private void ResetKpY_Click(object sender, EventArgs e) => KpY.Text = "0";

        private void StartZ_Click(object sender, EventArgs e)
        {
            // แปลง sender เป็นปุ่ม เพื่อให้เปลี่ยนสี/ข้อความได้
            Button btn = sender as Button;

            // --- กรณีที่ 1: กำลังทำงานอยู่ -> สั่งหยุด (PAUSE) ---
            if (_timerPidZ.Enabled)
            {
                _timerPidZ.Stop();

                // ถ้าต้องการให้ Pause แล้ว Reset ค่า PID ด้วย (เริ่มใหม่หมด) ให้เปิดบรรทัดนี้:
                // _outputX = 0; // อาจจะเคลียร์ค่า Output ด้วยถ้าต้องการ

                // เปลี่ยนหน้าตาปุ่มกลับเป็น "Start"
                if (btn != null)
                {
                    btn.Text = "Start PID X";
                    btn.BackColor = Color.LightGreen; // สีเขียว = พร้อมเริ่ม
                }
            }
            // --- กรณีที่ 2: หยุดอยู่ -> สั่งเริ่ม (START) ---
            else
            {
                // 1. อัปเดตค่า PID
                UpdatePidParams(_pidZ, KpX.Text, KiX.Text, KdX.Text);

                // 2. เริ่มคำนวณ
                _timerPidZ.Start();

                // 3. เริ่มส่งข้อมูล (ถ้ายังไม่ส่ง)
                if (!timerSender.Enabled && _controllerPort.IsOpen)
                {
                    timerSender.Start();
                }

                // เปลี่ยนหน้าตาปุ่มเป็น "Stop/Pause"
                if (btn != null)
                {
                    btn.Text = "Pause PID X";
                    btn.BackColor = Color.Salmon; // สีแดง/ส้ม = กำลังทำงาน
                }
            }
        }
        private void TuningZkp_Click(object sender, EventArgs e) => UpdatePidParams(_pidZ, KpZ.Text, KiZ.Text, KdZ.Text);
        private void ResetKpZ_Click(object sender, EventArgs e) => KpZ.Text = "0";

        // ==========================================
        // 9. Config & Helpers
        // ==========================================
        private void SaveSystemConfig()
        {
            try
            {
                double.TryParse(KpX.Text, out double kpx); _appConfig.PidX.Kp = kpx;
                double.TryParse(KiX.Text, out double kix); _appConfig.PidX.Ki = kix;
                double.TryParse(KdX.Text, out double kdx); _appConfig.PidX.Kd = kdx;

                double.TryParse(KpY.Text, out double kpy); _appConfig.PidY.Kp = kpy;
                double.TryParse(KiY.Text, out double kiy); _appConfig.PidY.Ki = kiy;
                double.TryParse(KdY.Text, out double kdy); _appConfig.PidY.Kd = kdy;

                double.TryParse(KpZ.Text, out double kpz); _appConfig.PidZ.Kp = kpz;
                double.TryParse(KiZ.Text, out double kiz); _appConfig.PidZ.Ki = kiz;
                double.TryParse(KdZ.Text, out double kdz); _appConfig.PidZ.Kd = kdz;

                AppConfig.Save(_appConfig, ConfigFilePath);
                MessageBox.Show($"Saved to: {ConfigFilePath}");
            }
            catch (Exception ex) { MessageBox.Show("Save Error: " + ex.Message); }
        }

        private void LoadSystemConfig()
        {
            try
            {
                _appConfig = AppConfig.Load(ConfigFilePath);
                KpX.Text = _appConfig.PidX.Kp.ToString(); KiX.Text = _appConfig.PidX.Ki.ToString(); KdX.Text = _appConfig.PidX.Kd.ToString();
                KpY.Text = _appConfig.PidY.Kp.ToString(); KiY.Text = _appConfig.PidY.Ki.ToString(); KdY.Text = _appConfig.PidY.Kd.ToString();
                KpZ.Text = _appConfig.PidZ.Kp.ToString(); KiZ.Text = _appConfig.PidZ.Ki.ToString(); KdZ.Text = _appConfig.PidZ.Kd.ToString();
            }
            catch { }
        }

        private string ExtractPortName(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            var parts = input.Split(':');
            var port = parts[0].Trim();
            if (!port.StartsWith("COM") && input.Contains("(COM"))
            {
                int idx = input.IndexOf("(COM");
                return input.Substring(idx + 1, 4).TrimEnd(')');
            }
            return port;
        }

        // ==========================================
        // 10. Buttons & UI Events
        // ==========================================
        private void btnCalMagnati_Click(object sender, EventArgs e)
        {
            double lat = 0, lon = 0;

            // 1. รับค่าละติจูด/ลองจิจูดจากกล่องข้อความ
            if (double.TryParse(tbxLat.Text, out lat) && double.TryParse(tbxLon.Text, out lon) )
            {
                try
                {
                    // 2. ใช้เครื่องคิดเลขตัวกลางที่ประกาศไว้แล้ว (ไม่ต้อง new Wmm2020 ใหม่ทุกรอบ)
                    // _geomagnetismCalculator ถูกสร้างใน InitializeSystem() แล้ว

                    Geo.Coordinate coordinate = new Geo.Coordinate(lat, lon);

                    // คำนวณค่าสนามแม่เหล็ก
                    GeomagnetismResult result = _geomagnetismCalculator.TryCalculate(coordinate, DateTime.UtcNow);

                    // 3. แสดงผลลัพธ์
                    tbxCal.Clear();
                    tbxCal.Text += "--- Manual Calculation ---" + Environment.NewLine;
                    tbxCal.Text += "Model: WMM2025" + Environment.NewLine; 
                    tbxCal.Text += "Declination: " + result.Declination.ToString("F4") + Environment.NewLine;
                    tbxCal.Text += "Inclination: " + result.Inclination.ToString("F4") + Environment.NewLine;
                    tbxCal.Text += "Horizontal Intensity: " + result.HorizontalIntensity.ToString("F4") + Environment.NewLine;
                    tbxCal.Text += "Total Intensity: " + result.TotalIntensity.ToString("F4") + Environment.NewLine;
                    tbxCal.Text += "X: " + result.X.ToString("F4") + Environment.NewLine;
                    tbxCal.Text += "Y: " + result.Y.ToString("F4") + Environment.NewLine;
                    tbxCal.Text += "Z: " + result.Z.ToString("F4") + Environment.NewLine;
                    tbxCal.Text += "Time: " + DateTime.UtcNow.ToString() + Environment.NewLine;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Calculation Error: " + ex.Message);
                }
            }
            else
            {
                // แจ้งเตือนถ้าใส่เลขผิด
                tbxLon.Text = "Invalid Input";
                tbxLat.Text = "Invalid Input";
                MessageBox.Show("Please enter valid Latitude and Longitude numbers.");
            }
        }
        private void TimeSim_label_Click(object sender, EventArgs e) { if (!string.IsNullOrEmpty(TimeSim_label.Text)) Clipboard.SetText(TimeSim_label.Text); }

        private void button4_Click(object sender, EventArgs e)
        {
            try
            {
                _satService.SetTLE(tbx4.Text, tbx5.Text, tbx6.Text);
                if (_seriesSatTrack == null)
                {
                    if (plotViewInt2.Model == null) plotViewInt2.Model = new PlotModel { Title = "Map" };
                    _seriesSatTrack = new LineSeries { MarkerType = MarkerType.Circle, Color = OxyColors.Blue, StrokeThickness = 0 };
                    plotViewInt2.Model.Series.Add(_seriesSatTrack);
                }
                SatPosTimer.Start();
                if (Track != null) Track.Text = "Stop Tracking";
                MessageBox.Show("Tracking Started");
            }
            catch (Exception ex) { MessageBox.Show("TLE Error: " + ex.Message); }
        }

        private void Write_Btn_Click(object sender, EventArgs e)
        {
            if (!_isLogging)
            {
                string name = Name_Txb.Text.Trim();
                if (string.IsNullOrEmpty(name)) name = "DataLog";
                if (name.EndsWith(".csv")) name = name.Substring(0, name.Length - 4);
                _logBaseName = name;

                try
                {
                    lock (_fileLock) { OpenLogWriter(); }
                    _isLogging = true; _logCount = 0;
                    Write_Btn.Text = "STOP Saving"; Write_Btn.BackColor = Color.Salmon;
                    MessageBox.Show($"Started logging to: {_logFileName}");
                }
                catch (Exception ex) { MessageBox.Show($"File Error: {ex.Message}"); }
            }
            else
            {
                _isLogging = false;
                lock (_fileLock) { _logWriter?.Dispose(); _logWriter = null; }
                Write_Btn.Text = "Write / Start"; Write_Btn.BackColor = Color.LightGray;
                MessageBox.Show($"Logging Stopped. Total: {_logCount} rows.");
            }
        }

        private void ZerorizeBtn_Click(object sender, EventArgs e)
        {
            // 1. ส่งคำสั่ง Reset ไปที่ Hardware (ตามโค้ดเดิม)
            if (_sensorManager.IsOpen)
            {
                try
                {
                    // ส่งคำสั่ง Zerorize (Toggle)
                    _sensorManager.Write(new byte[] { 0x2A, 0x30, 0x30, 0x5A, 0x4E, 0x0D }); // 4E = ON/Toggle //4
                    Console.WriteLine("Zero Toggle Sent.");
                }
                catch (Exception ex) { MessageBox.Show("Error sending command: " + ex.Message); }
            }
            else
            {
                MessageBox.Show("Please connect sensor first");
                return;
            }

            // 2. อัปเดตค่า Reference ใน Software (ถ้าต้องการ Software Zero ด้วย)
            // _sensorService.SetZero(_magX_nT, _magY_nT, _magZ_nT); 
            // ^ เปิดบรรทัดบนนี้ถ้าต้องการให้โปรแกรมจำค่าปัจจุบันเป็น 0 ด้วย (นอกเหนือจาก Hardware Reset)
        }

        private void SendBtn_Click_2(object sender, EventArgs e)
        {
            if (_sensorManager.IsOpen) { try { _sensorManager.Write(new byte[] { 0xAA, 0xBB, 0xCC }); } catch { } }
        }

        private void capture_Click(object sender, EventArgs e)
        {
            try
            {
                var exporter = new PngExporter { Width = 1000, Height = 600 };
                exporter.ExportToFile(plotViewX.Model, $"GraphX_{DateTime.Now:HHmmss}.png");
                MessageBox.Show("Graph Captured!");
            }
            catch (Exception ex) { MessageBox.Show("Capture Error: " + ex.Message); }
        }

        
        private void btnGen_Click(object sender, EventArgs e) { /* Optional: Reset Model */ }

        // Mapped UI Events (Boilerplate)
        private void saveX2_Click(object sender, EventArgs e) => SaveSystemConfig();
        private void saveY2_Click(object sender, EventArgs e) => SaveSystemConfig();
        private void saveZ2_Click(object sender, EventArgs e) => SaveSystemConfig();
        private void readX2_Click(object sender, EventArgs e) => LoadSystemConfig();
        private void readY2_Click(object sender, EventArgs e) => LoadSystemConfig();
        private void readZ2_Click(object sender, EventArgs e) => LoadSystemConfig();
        private void btnRefreshPorts_Click(object sender, EventArgs e) => UpdatePortList();
        private void timer1_Tick(object sender, EventArgs e) => textBoxTime.Text = DateTime.UtcNow.ToString();
        private void button2_Click(object sender, EventArgs e) => timerUTC.Enabled = !timerUTC.Enabled;
        private void SatPosReset_Click(object sender, EventArgs e) { _simTimeOffset = 0; TimeSpeed.Value = 0; textSpeed.Text = "0"; }
        private void SetSpeed_Btn_Click(object sender, EventArgs e) { if (int.TryParse(textSpeed.Text, out int v)) TimeSpeed.Value = v; }
        private void TimeSpeed_Scroll(object sender, EventArgs e) => textSpeed.Text = TimeSpeed.Value.ToString();
        private void textBoxErrorX_per_TextChanged(object sender, EventArgs e) => UpdateErrorStyle(sender as TextBox);
        private void textBoxErrorY_per_TextChanged(object sender, EventArgs e) => UpdateErrorStyle(sender as TextBox);
        private void textBoxErrorZ_per_TextChanged(object sender, EventArgs e) => UpdateErrorStyle(sender as TextBox);
        private void KiX_TextChanged(object sender, EventArgs e) => ValidateDoubleInput(sender as TextBox);
        private void KdX_TextChanged(object sender, EventArgs e) => ValidateDoubleInput(sender as TextBox);
        private void KpX_TextChanged(object sender, EventArgs e) => ValidateDoubleInput(sender as TextBox);
        private void KpY_TextChanged(object sender, EventArgs e) => ValidateDoubleInput(sender as TextBox);
        private void KiY_TextChanged(object sender, EventArgs e) => ValidateDoubleInput(sender as TextBox);
        private void KdY_TextChanged(object sender, EventArgs e) => ValidateDoubleInput(sender as TextBox);
        private void KpZ_TextChanged(object sender, EventArgs e) => ValidateDoubleInput(sender as TextBox);
        private void KiZ_TextChanged(object sender, EventArgs e) => ValidateDoubleInput(sender as TextBox);
        private void KdZ_TextChanged(object sender, EventArgs e) => ValidateDoubleInput(sender as TextBox);
        private void tbxSatLat_TextChanged(object sender, EventArgs e) => ValidateDoubleInput(sender as TextBox);
        private void tbxX_TextChanged(object sender, EventArgs e) => ValidateDoubleInput(sender as TextBox);
        private void tbxY_TextChanged(object sender, EventArgs e) => ValidateDoubleInput(sender as TextBox);
        private void tbxZ_TextChanged(object sender, EventArgs e) => ValidateDoubleInput(sender as TextBox);
        private void tbx4_TextChanged(object sender, EventArgs e) { }
        private void tbx5_TextChanged(object sender, EventArgs e) { }
        private void timer_sensor_Tick(object sender, EventArgs e) { if (_sensorManager.IsOpen && !_sensorManager.IsSensorReady) try { _sensorManager.Write(new byte[] { 0x2A, 0x30, 0x30, 0x57, 0x45, 0x0D }); } catch { } } //0x2A, 0x30, 0x30, 0x57, 0x45, 0x0D
        private void cboControllerPort_SelectedIndexChanged(object sender, EventArgs e) { }
        private void btnMasterReset_Click(object sender, EventArgs e) { _graphX?.Clear(); _graphY?.Clear(); _graphZ?.Clear(); _pidX.Reset(); _pidY.Reset(); _pidZ.Reset(); _calcService.ResetFilters(); MessageBox.Show("System Reset!"); }
        private void btnResetKF_X_Click(object sender, EventArgs e) { _calcService.ResetFilterX(); debug_label_rx.Text = "Filter X reset"; }
        private void btnResetKF_Y_Click(object sender, EventArgs e) { _calcService.ResetFilterY(); debug_label_rx.Text = "Filter Y reset"; }
        private void btnResetKF_Z_Click(object sender, EventArgs e) { _calcService.ResetFilterZ(); debug_label_rx.Text = "Filter Z reset"; }
        private void Track_Click(object sender, EventArgs e) { if (SatPosTimer.Enabled) { SatPosTimer.Stop(); Track.Text = "Start Tracking"; } else { SatPosTimer.Start(); Track.Text = "Stop Tracking"; } }
        private void cboSatelliteList_SelectedIndexChanged_1(object sender, EventArgs e) { if (cboSatelliteList.SelectedItem is SatelliteInfo sat && !string.IsNullOrEmpty(sat.Line1)) { tbx4.Text = sat.Name; tbx5.Text = sat.Line1; tbx6.Text = sat.Line2; } }
        private void btnTimeNow_Click(object sender, EventArgs e) => timerUTC.Enabled = !timerUTC.Enabled;
        private void buttonSetKFR_Click(object sender, EventArgs e)
        {
            try
            {
                // ตรวจสอบว่ากดปุ่มไหน และอัปเดตค่า R ของ Filter แกนนั้น

                // แกน X
                if (sender == buttonSetKFR_X && double.TryParse(textBoxKFR_X.Text, out double rx))
                {
                    // อัปเดตค่า R เข้าไปใน Filter ตัวจริงที่ CalculationService ใช้ (ไม่ใช่ orphan filter อีกต่อไป)
                    _calcService.SetMeasurementNoiseX(rx);
                    MessageBox.Show($"X Filter R updated to: {rx}");
                }
                // แกน Y
                else if (sender == buttonSetKFR_Y && double.TryParse(textBoxKFR_Y.Text, out double ry))
                {
                    _calcService.SetMeasurementNoiseY(ry);
                    MessageBox.Show($"Y Filter R updated to: {ry}");
                }
                // แกน Z
                else if (sender == buttonSetKFR_Z && double.TryParse(textBoxKFR_Z.Text, out double rz))
                {
                    _calcService.SetMeasurementNoiseZ(rz);
                    MessageBox.Show($"Z Filter R updated to: {rz}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Invalid Input: " + ex.Message);
            }
        }
        private void TuningXKi_Click(object sender, EventArgs e) => UpdatePidParams(_pidX, KpX.Text, KiX.Text, KdX.Text);
        private void TuningXkd_Click(object sender, EventArgs e) => UpdatePidParams(_pidX, KpX.Text, KiX.Text, KdX.Text);
        private void ResetKiX_Click(object sender, EventArgs e) => KiX.Text = "0";
        private void ResetKdX_Click(object sender, EventArgs e) => KdX.Text = "0";
        private void TuningYki_Click(object sender, EventArgs e) => UpdatePidParams(_pidY, KpY.Text, KiY.Text, KdY.Text);
        private void TuningYkd_Click(object sender, EventArgs e) => UpdatePidParams(_pidY, KpY.Text, KiY.Text, KdY.Text);
        private void ResetKiY_Click(object sender, EventArgs e) => KiY.Text = "0";
        private void ResetKdY_Click(object sender, EventArgs e) => KdY.Text = "0";
        private void TuningZki_Click(object sender, EventArgs e) => UpdatePidParams(_pidZ, KpZ.Text, KiZ.Text, KdZ.Text);
        private void TuningZkd_Click(object sender, EventArgs e) => UpdatePidParams(_pidZ, KpZ.Text, KiZ.Text, KdZ.Text);
        private void ResetKiZ_Click(object sender, EventArgs e) => KiZ.Text = "0";
        private void ResetKdZ_Click(object sender, EventArgs e) => KdZ.Text = "0";
        private void buttonSetTarget_Click(object sender, EventArgs e)
        {
            if (double.TryParse(textBoxSetpointX.Text, out double val))
            {
                if (!double.TryParse(textLowerBoundX.Text, out double lowerX) || !double.TryParse(textUpperBoundX.Text, out double upperX))
                {
                    MessageBox.Show("Lower/Upper Bound ต้องเป็นตัวเลข");
                    return;
                }
                if (lowerX >= upperX)
                {
                    MessageBox.Show("Lower Bound ต้องน้อยกว่า Upper Bound");
                    return;
                }
                // 1. อัปเดตค่าเป้าหมาย
                _setpointX = val;
                _pidX.MinOutput = lowerX;
                _pidX.MaxOutput = upperX;
                // 2. โชว์ค่าที่ตั้งไว้
                textSetpointXMag.Text = _setpointX.ToString("F3");

                // 3. [สำคัญ] หยุดโหมดติดตามดาวเทียม (Manual Override)
                // ต้องหยุด SatPosTimer ไม่งั้นเดี๋ยวค่าจากดาวเทียมจะมาทับค่าที่เราตั้งเอง
                if (SatPosTimer.Enabled)
                {
                    SatPosTimer.Stop();
                    if (Track != null) Track.Text = "Start Tracking"; // คืนชื่อปุ่ม
                }
            }
        }

        // ปุ่ม Set Target แกน Y
        private void buttonSetTargetY_Click(object sender, EventArgs e)
        {
            
            if (double.TryParse(textBoxSetpointY.Text, out double val))
            {
                if (!double.TryParse(textLowerBoundY.Text, out double lowerY) || !double.TryParse(textUpperBoundY.Text, out double upperY))
                {
                    MessageBox.Show("Lower/Upper Bound ต้องเป็นตัวเลข");
                    return;
                }
                if (lowerY >= upperY)
                {
                    MessageBox.Show("Lower Bound ต้องน้อยกว่า Upper Bound");
                    return;
                }
                _setpointY = val;
                _pidY.MinOutput = lowerY;
                _pidY.MaxOutput = upperY;
                textSetpointYMag.Text = _setpointY.ToString("F3");


                // หยุด Tracking เหมือนกัน
                if (SatPosTimer.Enabled)
                {
                    SatPosTimer.Stop();
                    if (Track != null) Track.Text = "Start Tracking";
                }
            }
        }

        private void textSysY2_TextChanged(object sender, EventArgs e)
        {

        }

        private void textLowerBoundX_TextChanged(object sender, EventArgs e)
        {

        }

        private void textSensorX2_TextChanged(object sender, EventArgs e)
        {

        }

        private void button2_Click_1(object sender, EventArgs e) //un0
        {
            // 1. ส่งคำสั่ง Reset ไปที่ Hardware (ตามโค้ดเดิม)
            if (_sensorManager.IsOpen)
            {
                try
                {
                    // ส่งคำสั่ง Zerorize (Toggle)
                    _sensorManager.Write(new byte[] { 0x2A, 0x30, 0x30, 0x5A, 0x46, 0x0D }); // 4E = ON/Toggle //46 = OFF/Toggle
                    Console.WriteLine("UnZero Toggle Sent.");
                }
                catch (Exception ex) { MessageBox.Show("Error sending command: " + ex.Message); }
            }
            else
            {
                MessageBox.Show("Please connect sensor first");
                return;
            }
        }

        private void Name_Txb_TextChanged(object sender, EventArgs e)
        {

        }
        private void Count_label_Click(object sender, EventArgs e)
        {

        }
        private readonly object _dataLock = new object(); // lock ข้อมูล

        private void plotViewX_Click(object sender, EventArgs e)
        {

        }

        private readonly object _fileLock = new object(); // lock ไฟล์

        private void button3_Click(object sender, EventArgs e)
        
        {
            try
            {
                
                string logsDir = Path.Combine(Application.StartupPath, "logs");
                Directory.CreateDirectory(logsDir);

                string snapname = Name_Txb.Text.Trim();
                if (string.IsNullOrEmpty(snapname)) snapname = $"DataLog_{DateTime.Now:yyyyMMdd_HHmmss}";
                if (!snapname.EndsWith(".csv")) snapname += ".csv";
                string snapPath = Path.Combine(logsDir, snapname);
                string rawSnapPath = Path.Combine(logsDir, "Raw" + snapname);
                bool fileExists = File.Exists(snapPath);
                bool rawfileExists = File.Exists(rawSnapPath);
                // เขียน header แค่ครั้งเดียว
                if (!fileExists)
                {
                    File.WriteAllText(snapPath,
                        "Timestamp,MagX,MagY,MagZ\n") ;
                       // "RawX,RawY,RawZ,SetX,SetY,SetZ,ErrX,ErrY,ErrZ,OutX,OutY,OutZ,KpX,KiX,KdX,KpY,KiY,KdY,KpZ,KiZ,KdZ\n");
                }
                if (!rawfileExists)
                {
                    File.WriteAllText(rawSnapPath,
                        "Timestamp,RawX,RawY,RawZ\n");
                        //"SetX,SetY,SetZ,ErrX,ErrY,ErrZ,OutX,OutY,OutZ,MagX,MagY,MagZ,KpX,KiX,KdX,KpY,KiY,KdY,KpZ,KiZ,KdZ\n");
                }
                string dataLine;
                string rawdataLine;

                lock (_dataLock)
                {
                    string timeStamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    dataLine =
                        $"{timeStamp},{_magX_nT:F2},{_magY_nT:F2},{_magZ_nT:F2}\n";
                    //$"{_RawmagX_nT:F2},{_RawmagY_nT:F2},{_RawmagZ_nT:F2}," +
                    //$"{_setpointX:F2},{_setpointY:F2},{_setpointZ:F2}," +
                    //$"{textBoxErrorX.Text},{textBoxErrorY.Text},{textBoxErrorZ.Text}," +
                    //$"{_outputX:F2},{_outputY:F2},{_outputZ:F2}," +
                    //$"{KpX.Text},{KiX.Text},{KdX.Text}," +
                    //$"{KpY.Text},{KiY.Text},{KdY.Text}," +
                    //$"{KpZ.Text},{KiZ.Text},{KdZ.Text}\n";
                    rawdataLine =
                        $"{timeStamp},{_RawmagX_nT:F2},{_RawmagY_nT:F2},{_RawmagZ_nT:F2}\n";
                       
                        //$"{_setpointX:F2},{_setpointY:F2},{_setpointZ:F2}," +
                        //$"{textBoxErrorX.Text},{textBoxErrorY.Text},{textBoxErrorZ.Text}," +
                        //$"{_outputX:F2},{_outputY:F2},{_outputZ:F2}," +
                        //$"{KpX.Text},{KiX.Text},{KdX.Text}," +
                        //$"{KpY.Text},{KiY.Text},{KdY.Text}," +
                        //$"{KpZ.Text},{KiZ.Text},{KdZ.Text}\n";
                }
                lock (_fileLock)
                {
                    File.AppendAllText(snapPath, dataLine);
                    File.AppendAllText(rawSnapPath, rawdataLine);
                }
                _logCount++;
                if (_logCount == 21)
                {
                    _logCount = 0;

                }
                if (Count_label != null)
                {
                    this.BeginInvoke(new Action(() => Count_label.Text = _logCount.ToString()));
                }

            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Log Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ปุ่ม Set Target แกน Z
        private void buttonSetTargetZ_Click(object sender, EventArgs e)
        {
            
            if (double.TryParse(textBoxSetpointZ.Text, out double val))
            {
                if (!double.TryParse(textLowerBoundZ.Text, out double lowerZ) || !double.TryParse(textUpperBoundZ.Text, out double upperZ))
                {
                    MessageBox.Show("Lower/Upper Bound ต้องเป็นตัวเลข");
                    return;
                }
                if (lowerZ >= upperZ)
                {
                    MessageBox.Show("Lower Bound ต้องน้อยกว่า Upper Bound");
                    return;
                }
                _setpointZ = val;
                _pidZ.MinOutput = lowerZ;
                _pidZ.MaxOutput = upperZ;
                textSetpointZMag.Text = _setpointZ.ToString("F3");

                // หยุด Tracking เหมือนกัน
                if (SatPosTimer.Enabled)
                {
                    SatPosTimer.Stop();
                    if (Track != null) Track.Text = "Start Tracking";
                }
            }
        }

        private void debug_label_x_Click(object sender, EventArgs e)
        {

        }

        private void tracktimer_Tick(object sender, EventArgs e) { }
        private void Avg_timer_Tick(object sender, EventArgs e) { }
        private void timerRefreshUI_Tick(object sender, EventArgs e) { }
        private void timerX_Tick(object sender, EventArgs e) { }
        private void timerY_Tick(object sender, EventArgs e) { }
        private void timerZ_Tick(object sender, EventArgs e) { }
    }
}