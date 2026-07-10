namespace IGRF_Interface.Models
{
    // ตัวถังสำหรับเก็บข้อมูลดิบจาก Sensor (ก่อน Filter)
    public class RawSensorData
    {
        public double x { get; set; }
        public double y { get; set; }
        public double z { get; set; }

        // Constructor แบบง่าย
        public RawSensorData(double xVal, double yVal, double zVal)
        {
            x = xVal;
            y = yVal;
            z = zVal;
        }

        // Constructor เปล่า (เผื่อใช้ทั่วไป)
        public RawSensorData() { }
    }
}