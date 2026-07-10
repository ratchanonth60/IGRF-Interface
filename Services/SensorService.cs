using System;

namespace IGRF_Interface.Services
{
    public class SensorService
    {
        // ค่าคงที่จากโค้ดเดิม
        //private const double ALPHA = 1347.35;
        //private const double BETA = 4061.63;
        //private const double GAMMA = -1418.96; // แก้คำผิดจาก gammar
        //private const double SIGMA = 1;

        //1349.5,
        //4110.95,
        //- 1343.37,
        private const double SCALE = 20.0 / 3.0;

        private readonly double[] hardIron =
        {//0,0,0
        1349.5,
        4110.95,
        - 1343.37,

        };

        // Soft-iron 3x3 matrix
        private readonly double[,] softIron =
        {
        {0.9958, -0.0050, 0.0064},
        {-0.050, 1.0042, -0.0087},
        {0.0064, -0.0087, 1.0003}
        };

        // ตัวแปรสำหรับเก็บ Reference (Zero Offset)
        public double ReferenceX { get; set; } = 0;
        public double ReferenceY { get; set; } = 0;
        public double ReferenceZ { get; set; } = 0;

        public double LastRawX { get; private set; }
        public double LastRawY { get; private set; }
        public double LastRawZ { get; private set; }

        public RawSensorData ProcessData(byte[] packet)
        { 
            // ตรวจสอบความยาวแพ็กเก็ต (โค้ดเดิมใช้ 7 bytes และเช็คตัวสุดท้ายเป็น 13)
            if (packet == null || packet.Length < 7) return new RawSensorData();
            // -------- RAW --------
            short rawX = BitConverter.ToInt16(new[] { packet[1], packet[0] }, 0);
            short rawY = BitConverter.ToInt16(new[] { packet[3], packet[2] }, 0);
            short rawZ = BitConverter.ToInt16(new[] { packet[5], packet[4] }, 0);

            // เก็บค่า RAW ล่าสุด

            // -------- SCALE --------
            double[] mag =
            {
            rawX * SCALE,
            rawY * SCALE,
            rawZ * SCALE
            };
            LastRawX = mag[0];
            LastRawY = mag[1];
            LastRawZ = mag[2];

            // -------- HARD IRON --------
            double[] magHI =
            {
            mag[0] - hardIron[0] - ReferenceX,
            mag[1] - hardIron[1] - ReferenceY,
            mag[2] - hardIron[2] - ReferenceZ
            };

            // -------- SOFT IRON --------
            double[] magCal = new double[3];
            for (int i = 0; i < 3; i++)
            {
                magCal[i] =
                    softIron[i, 0] * magHI[0] +
                    softIron[i, 1] * magHI[1] +
                    softIron[i, 2] * magHI[2];
            }
            //// Logic เดิม: แปลง Byte -> Int16 -> คำนวณสูตร -> ลบ Reference

            //// แกน X (Byte 0-1)
            //byte[] mx = { packet[1], packet[0] }; // สลับ Little/Big Endian ตามโค้ดเดิม (Array.Reverse)
            //double magX_raw = BitConverter.ToInt16(mx, 0);
            //double magX_nT = ((magX_raw * (20.0 / 3.0) - ALPHA) * SIGMA) - ReferenceX;

            //// แกน Y (Byte 2-3)
            //byte[] my = { packet[3], packet[2] };
            //double magY_raw = BitConverter.ToInt16(my, 0);
            //double magY_nT = (magY_raw * (20.0 / 3.0) - BETA) - ReferenceY;

            //// แกน Z (Byte 4-5)
            //byte[] mz = { packet[5], packet[4] };
            //double magZ_raw = BitConverter.ToInt16(mz, 0);
            //double magZ_nT = (magZ_raw * (20.0 / 3.0) - GAMMA) - ReferenceZ;

            return new RawSensorData
            {
                MagX = magCal[0], //magX_nT,
                MagY = magCal[1], //magY_nT,
                MagZ = magCal[2], //magZ_nT
            };
        }

        // ฟังก์ชันสำหรับปุ่ม Zerorize (Set Current Value as Reference)
        public void SetZero(double currentX, double currentY, double currentZ)
        {
            ReferenceX = currentX + ReferenceX; // บวกทบไปเรื่อยๆ หรือจะ = currentX เลยก็ได้แล้วแต่ Logic
            ReferenceY = currentY + ReferenceY;
            ReferenceZ = currentZ + ReferenceZ;
        }
    }

    public struct RawSensorData
    {
        public double MagX { get; set; }
        public double MagY { get; set; }
        public double MagZ { get; set; }
    }
}