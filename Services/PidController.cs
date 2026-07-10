using System;

namespace IGRF_Interface.Services
{
    public class PidController
    {
        // Parameter หลัก
        public double Kp { get; set; } = 0;
        public double Ki { get; set; } = 0;
        public double Kd { get; set; } = 0;

        // [สำคัญ] ต้องตั้งค่าเริ่มต้น ไม่่งั้นมันจะ Clamp เหลือ 0 หมด
        public double MaxOutput { get; set; } = 100.0;
        public double MinOutput { get; set; } = -100.0;

        // ตัวแปรสำหรับจำค่าเก่า
        private double _prevError = 0;
        private double _integral = 0;

        public double Calculate(double setpoint, double measurement)
        {
            double error = setpoint - measurement;

            // 1. Proportional Term
            double pOut = Kp * error;

            // 2. Integral Term (พร้อมกัน Windup)
            _integral += error;

            // [Improvement] ป้องกัน Integral Windup แบบง่าย
            // ถ้าค่า I สะสมเยอะเกินไปจนคูณ Ki แล้วเกิน MaxOutput ให้ตัดทิ้ง
            // (หรือจะใช้วิธี Clamp ค่า _integral โดยตรงก็ได้)
            double iOut = Ki * _integral;

            // Limit iOut ไม่ให้เกิน Max/Min เพื่อไม่ให้สะสมค่าเกินความจำเป็น
            if (iOut > MaxOutput)
            {
                iOut = MaxOutput;
                _integral = MaxOutput / (Ki != 0 ? Ki : 1); // ย้อนกลับไปแก้ integral ไม่ให้บวม
            }
            else if (iOut < MinOutput)
            {
                iOut = MinOutput;
                _integral = MinOutput / (Ki != 0 ? Ki : 1);
            }

            // 3. Derivative Term
            double derivative = error - _prevError;
            double dOut = Kd * derivative;

            // รวมผล
            double output = pOut + iOut + dOut;

            // 4. Clamp Output สุดท้าย (Safety)
            if (output > MaxOutput) output = MaxOutput;
            else if (output < MinOutput) output = MinOutput;

            // จำค่า Error ไว้รอบหน้า
            _prevError = error;

            return output;
        }

        public void Reset()
        {
            _prevError = 0;
            _integral = 0;
        }
    }
}