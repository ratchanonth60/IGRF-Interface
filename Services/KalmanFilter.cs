using System;

namespace IGRF_Interface.Services // ถ้าไม่ได้แยก Folder ให้ลบ .Services ออก หรือแก้ให้ตรงกับ Form1
{
    public class KalmanFilter
    {
        // --- Configuration Parameters (ปรับจูนได้ตลอดเวลา) ---

        /// <summary>
        /// A: System Model (State Transition). 
        /// ปกติ = 1 สำหรับการวัดค่าคงที่
        /// </summary>
        public double A { get; set; } = 1;

        /// <summary>
        /// H: Measurement Model (Scale Factor). 
        /// ปกติ = 1 ถ้าค่าจาก Sensor เป็นหน่วยเดียวกับ State
        /// </summary>
        public double H { get; set; } = 1;

        /// <summary>
        /// Q: Process Noise Covariance (ความแปรปรวนของระบบ)
        /// - ค่าน้อย: เชื่อว่าระบบเสถียร เปลี่ยนแปลงช้า (Output นิ่งแต่ Delay)
        /// - ค่ามาก: เชื่อว่าระบบมีการเปลี่ยนแปลงไว (Output ไวแต่แกว่ง)
        /// </summary>
        public double Q { get; set; } = 1;

        /// <summary>
        /// R: Measurement Noise Covariance (ความแปรปรวนของ Sensor)
        /// - ค่าน้อย: เชื่อค่าจาก Sensor มาก (กรองน้อย)
        /// - ค่ามาก: ไม่เชื่อ Sensor (กรองหนัก, กราฟเรียบ)
        /// </summary>
        public double R { get; set; } = 100;
        public double R_Val
        {
            get { return R; }
            set
            {
                // กัน R <= 0 ซึ่งจะทำให้ตัวหาร Kalman Gain เป็น 0/ติดลบ แล้ว NaN/Infinity ไหลเข้า State เงียบๆ
                if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value), "R ต้องมากกว่า 0");
                R = value;
            }
        }

        // --- State Variables (อ่านค่าได้อย่างเดียว) ---

        /// <summary>
        /// X: Estimated State (ค่าปัจจุบันที่กรองแล้ว)
        /// </summary>
        public double State { get; private set; }

        /// <summary>
        /// P: Error Covariance (ค่าความคลาดเคลื่อนที่คาดการณ์)
        /// </summary>
        public double Covariance { get; private set; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="initialState">ค่าเริ่มต้น (x0)</param>
        /// <param name="initialCovariance">ค่า P เริ่มต้น (ปกติ 1-10)</param>
        /// <param name="q">Process Noise (Default: 1)</param>
        /// <param name="r">Measurement Noise (Default: 100)</param>
        public KalmanFilter(double initialState, double initialCovariance = 1, double q = 1, double r = 100)
        {
            this.State = initialState;
            this.Covariance = initialCovariance;
            this.Q = q;
            this.R = r;
        }

        /// <summary>
        /// คำนวณค่ากรองสัญญาณ (Standard Predict + Update)
        /// </summary>
        /// <param name="measurement">ค่าที่อ่านได้จาก Sensor (z)</param>
        /// <param name="controlInput">Control Input (u) - ใส่ 0 ถ้าไม่มีแรงภายนอกกระทำ</param>
        /// <returns>ค่าที่กรองแล้ว (Filtered State)</returns>
        public double Filter(double measurement, double controlInput = 0)
        {
            // 1. Time Update (Prediction) - ทำนายอนาคต
            // x = A*x + B*u (ในที่นี้ B สมมติเป็น 1 ถ้ามี controlInput)
            double x_pred = (A * State) + controlInput;
            double p_pred = (A * Covariance * A) + Q;

            // 2. Measurement Update (Correction) - แก้ไขด้วยความจริง
            // K = Kalman Gain
            double K = (p_pred * H) / ((H * p_pred * H) + R);

            // Update State (x)
            State = x_pred + K * (measurement - (H * x_pred));

            // Update Covariance (P)
            Covariance = (1 - (K * H)) * p_pred;

            return State;
        }

        /// <summary>
        /// รีเซ็ตค่า Filter ใหม่ (เช่น กรณี Sensor หลุดแล้วต่อใหม่)
        /// </summary>
        public void Reset(double initialState, double initialCovariance = 1)
        {
            this.State = initialState;
            this.Covariance = initialCovariance;
        }
    }
}