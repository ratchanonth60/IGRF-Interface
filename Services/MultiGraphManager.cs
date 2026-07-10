using System;
using System.Collections.Generic;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.WindowsForms;

namespace IGRF_Interface.Services
{
    /// <summary>
    /// ตัวจัดการกราฟแบบหลาย Series (Multi-Series) สำหรับกรณีที่ต้องการมากกว่า 2 เส้น
    /// เช่น เซนเซอร์ Magson ที่มีค่า X, Y, Z, |B|
    /// </summary>
    public class MultiGraphManager
    {
        private readonly PlotView _plotView;
        private readonly List<LineSeries> _series = new List<LineSeries>();

        // เปลี่ยนเป็น Property เพื่อให้ปรับแก้ได้จากภายนอก
        public int MaxPoints { get; set; } = 500;
        public double StrokeThickness { get; set; } = 1.5;

        public MultiGraphManager(PlotView plotView, string title, params (string Name, OxyColor Color)[] series)
        {
            _plotView = plotView ?? throw new ArgumentNullException(nameof(plotView));
            InitializePlot(title, series);
        }

        private void InitializePlot(string title, (string Name, OxyColor Color)[] series)
        {
            var model = new PlotModel
            {
                Title = title,
                TitleFontSize = 14,
                PlotAreaBorderColor = OxyColors.Gray, // กรอบกราฟสีเทาอ่อน ดูสะอาดตา
                IsLegendVisible = true
            };

            // Setup แกนเวลา (X Axis)
            model.Axes.Add(new DateTimeAxis
            {
                Position = AxisPosition.Bottom,
                StringFormat = "HH:mm:ss",
                IntervalType = DateTimeIntervalType.Seconds,
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot,
                Title = "Time"
            });

            // Setup แกนค่า (Y Axis) - เพิ่มเพื่อให้มี Grid ให้อ่านง่าย
            model.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot,
                Title = "Value (nT)"
            });

            // Setup Series ตามที่ผู้เรียกกำหนดมา
            if (series != null)
            {
                foreach (var s in series)
                {
                    var lineSeries = new LineSeries
                    {
                        Title = s.Name,
                        Color = s.Color,
                        StrokeThickness = this.StrokeThickness,
                        LineStyle = LineStyle.Solid
                    };
                    _series.Add(lineSeries);
                    model.Series.Add(lineSeries);
                }
            }

            _plotView.Model = model;
        }

        /// <summary>
        /// อัปเดตค่าล่าสุดของแต่ละ Series ตามลำดับที่ประกาศไว้ตอนสร้าง
        /// ถ้าจำนวนค่าที่ส่งมาไม่ตรงกับจำนวน Series จะอัปเดตเท่าที่มีข้อมูล (ไม่ throw)
        /// </summary>
        public void Update(params double[] values)
        {
            if (_plotView.Model == null || values == null) return;

            // ใช้ lock เพื่อป้องกันการแย่งกันใช้ข้อมูลระหว่าง UI Thread กับ Data Thread
            lock (_plotView.Model.SyncRoot)
            {
                double time = DateTimeAxis.ToDouble(DateTime.Now);
                int count = Math.Min(_series.Count, values.Length);

                for (int i = 0; i < count; i++)
                {
                    var s = _series[i];
                    s.Points.Add(new DataPoint(time, values[i]));

                    // จำกัดจำนวนจุด (Memory Management)
                    if (s.Points.Count > MaxPoints)
                        s.Points.RemoveAt(0);
                }
            }

            // สั่งวาดใหม่ (Thread-Safe update)
            _plotView.InvalidatePlot(true);
        }

        /// <summary>
        /// ล้างกราฟทั้งหมด (เผื่อกดปุ่ม Reset)
        /// </summary>
        public void Clear()
        {
            if (_plotView.Model == null) return;

            lock (_plotView.Model.SyncRoot)
            {
                foreach (var s in _series)
                    s.Points.Clear();
            }
            _plotView.InvalidatePlot(true);
        }
    }
}
