using Newtonsoft.Json;
using System.IO;

namespace IGRF_Interface.Models
{
    public class PidSettings
    {
        public double Kp { get; set; } = 0;
        public double Ki { get; set; } = 0;
        public double Kd { get; set; } = 0;
        public double MaxOutput { get; set; } = 100;
        public double MinOutput { get; set; } = -100;
        public double Setpoint { get; set; } = 0;
    }

    public class AppConfig
    {
        public PidSettings PidX { get; set; } = new PidSettings();
        public PidSettings PidY { get; set; } = new PidSettings();
        public PidSettings PidZ { get; set; } = new PidSettings();

        // Sensor 2 (Magson MFG Fluxgate Magnetometer ผ่าน TCP)
        public string Sensor2Ip { get; set; } = "192.168.1.100";
        public int Sensor2Port { get; set; } = 12345;

        // Helper Methods สำหรับ Save/Load
        public static void Save(AppConfig config, string path = "SystemConfig.json")
        {
            string json = JsonConvert.SerializeObject(config, Formatting.Indented);
            File.WriteAllText(path, json);
        }

        public static AppConfig Load(string path = "SystemConfig.json")
        {
            try
            {
                if (!File.Exists(path)) return new AppConfig(); // ถ้าไม่มีไฟล์ ให้คืนค่า Default (0,0,0)

                string json = File.ReadAllText(path);
                var config = JsonConvert.DeserializeObject<AppConfig>(json);
                return config ?? new AppConfig(); // กันเหนียวถ้าไฟล์ว่างเปล่า
            }
            catch
            {
                return new AppConfig(); // ถ้าโหลดพัง ให้คืนค่า Default ไปก่อน
            }
        }
    }
}