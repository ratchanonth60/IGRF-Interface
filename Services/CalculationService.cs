using System;
using IGRF_Interface.Models;
using IGRF_Interface.Utils; // เรียกใช้ KalmanFilter
using IGRF_Interface_Demo1._1.Models;

namespace IGRF_Interface.Services
{
    public class CalculationService
    {
        // ย้ายตัวแปร Filter มาไว้ที่นี่ (เพราะมันคือ Logic ไม่ใช่ UI)
        private KalmanFilter _filterX = new KalmanFilter(0, 1, 1, 100);
        private KalmanFilter _filterY = new KalmanFilter(0, 1, 1, 100);
        private KalmanFilter _filterZ = new KalmanFilter(0, 1, 1, 100);

        // เช็คว่าแต่ละแกนเคยมีตัวอย่างแรกเข้ามาแล้วหรือยัง (สำหรับ Spike Gate)
        private bool _hasSampleX = false;
        private bool _hasSampleY = false;
        private bool _hasSampleZ = false;

        // นับจำนวนครั้งที่ค่าดีดติดต่อกัน (ถ้าดีดค้างนานๆ = ค่าจริงเปลี่ยนระดับ ไม่ใช่ spike)
        private int _rejectCountX = 0;
        private int _rejectCountY = 0;
        private int _rejectCountZ = 0;

        // ค่าดีด (nT) ที่ถือว่าผิดปกติ ไม่ป้อนเข้า Filter (กันค่า Spike จาก Packet เพี้ยน)
        public double SpikeThreshold { get; set; } = 10000;

        // ถ้าค่าดีดติดต่อกันครบจำนวนนี้ ให้ถือว่าเป็นการเปลี่ยนระดับจริง แล้วยอมรับค่าใหม่
        public int MaxConsecutiveRejects { get; set; } = 10;

        // ท่อเชื่อม UI -> Filter ตัวจริง (แทนการ set ใส่ orphan filter ใน Form1)
        public void SetMeasurementNoiseX(double r) => _filterX.R_Val = r;
        public void SetMeasurementNoiseY(double r) => _filterY.R_Val = r;
        public void SetMeasurementNoiseZ(double r) => _filterZ.R_Val = r;

        // ฟังก์ชันคำนวณ (รับ Raw Data -> คืนค่า ProcessedData)
        // สังเกตว่าฟังก์ชันนี้ "ไม่รู้จัก UI" เลย (Pure Logic)
        public ProcessedData ProcessSensorData(RawSensorData raw, double setX, double setY, double setZ)
        {
            var data = new ProcessedData();

            // 1. Filter (ผ่าน Spike Gate ก่อน กันค่าดีดหลุดเข้าไปทำ State พัง)
            data.MagX = FilterAxis(_filterX, raw.MagX, ref _hasSampleX, ref _rejectCountX);
            data.MagY = FilterAxis(_filterY, raw.MagY, ref _hasSampleY, ref _rejectCountY);
            data.MagZ = FilterAxis(_filterZ, raw.MagZ, ref _hasSampleZ, ref _rejectCountZ);

            // 2. Calculate Error
            data.ErrorX = Math.Abs(setX - data.MagX);
            data.ErrorY = Math.Abs(setY - data.MagY);
            data.ErrorZ = Math.Abs(setZ - data.MagZ);

            // 3. Calculate %
            data.ErrorPerX = CalculatePercent(data.ErrorX, setX);
            data.ErrorPerY = CalculatePercent(data.ErrorY, setY);
            data.ErrorPerZ = CalculatePercent(data.ErrorZ, setZ);

            return data;
        }

        // ถ้าตัวอย่างแรกเข้ามาให้รับเสมอ หลังจากนั้นถ้าค่าดีดเกิน SpikeThreshold จาก State ปัจจุบัน
        // ให้ข้ามการ Filter() รอบนี้ แล้วคืนค่า State เดิมแทน (ไม่ป้อน outlier เข้า Kalman)
        // แต่ถ้าดีดติดต่อกันเกิน MaxConsecutiveRejects = ค่าจริงเปลี่ยนระดับ ให้ Reset filter ไปที่ค่าใหม่
        private double FilterAxis(KalmanFilter filter, double raw, ref bool hasSample, ref int rejectCount)
        {
            if (hasSample && Math.Abs(raw - filter.State) > SpikeThreshold)
            {
                rejectCount++;
                if (rejectCount < MaxConsecutiveRejects)
                {
                    return filter.State;
                }
                filter.Reset(raw, 1);
            }
            rejectCount = 0;
            hasSample = true;
            return filter.Filter(raw);
        }

        public void ResetFilters()
        {
            ResetFilterX();
            ResetFilterY();
            ResetFilterZ();
        }

        public void ResetFilterX()
        {
            _filterX.Reset(0, 1);
            _hasSampleX = false;
            _rejectCountX = 0;
        }

        public void ResetFilterY()
        {
            _filterY.Reset(0, 1);
            _hasSampleY = false;
            _rejectCountY = 0;
        }

        public void ResetFilterZ()
        {
            _filterZ.Reset(0, 1);
            _hasSampleZ = false;
            _rejectCountZ = 0;
        }

        private double CalculatePercent(double error, double setpoint)
        {
            return setpoint != 0 ? (error / Math.Abs(setpoint)) * 100 : 0;
        }
    }
}
