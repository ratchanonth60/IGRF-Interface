using System;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.WindowsForms;

namespace IGRF_Interface.Services
{
    public class GraphManager
    {
        private readonly PlotView _plotView;
        private LineSeries _setpointSeries;
        private LineSeries _measuredSeries;
        private readonly object _lockObj = new object(); // กุญแจล็อค Thread

        // เปลี่ยนเป็น Property เพื่อให้ปรับแก้ได้จากภายนอก
        public int MaxPoints { get; set; } = 500;
        public double StrokeThickness { get; set; } = 1.5;

        public GraphManager(PlotView plotView, string title)
        {
            _plotView = plotView ?? throw new ArgumentNullException(nameof(plotView));
            InitializePlot(title);
        }

        private void InitializePlot(string title)
        {
            var model = new PlotModel
            {
                Title = title,
                TitleFontSize = 14,
                PlotAreaBorderColor = OxyColors.Gray // กรอบกราฟสีเทาอ่อน ดูสะอาดตา
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

            // Setup Series
            _setpointSeries = new LineSeries
            {
                Title = "Setpoint",
                Color = OxyColors.Black,
                StrokeThickness = this.StrokeThickness,
                LineStyle = LineStyle.Solid
            };

            _measuredSeries = new LineSeries
            {
                Title = "Measured",
                Color = OxyColors.Red,
                StrokeThickness = this.StrokeThickness,
                LineStyle = LineStyle.Solid
            };
            model.Series.Add(_setpointSeries);
            model.Series.Add(_measuredSeries);
            _plotView.Model = model;
        }

        public void Update(double setpoint, double measured)
        {
            if (_plotView.Model == null) return;

            // ใช้ lock เพื่อป้องกันการแย่งกันใช้ข้อมูลระหว่าง UI Thread กับ Data Thread
            lock (_plotView.Model.SyncRoot)
            {
                double time = DateTimeAxis.ToDouble(DateTime.Now);

                _setpointSeries.Points.Add(new DataPoint(time, setpoint));
                _measuredSeries.Points.Add(new DataPoint(time, measured));

                // จำกัดจำนวนจุด (Memory Management)
                if (_setpointSeries.Points.Count > MaxPoints)
                    _setpointSeries.Points.RemoveAt(0);

                if (_measuredSeries.Points.Count > MaxPoints)
                    _measuredSeries.Points.RemoveAt(0);
            }

            // สั่งวาดใหม่ (Thread-Safe update)
            _plotView.InvalidatePlot(true);
        }
        public void Updatesensor(double measured)
        {
            if (_plotView.Model == null) return;

            // ใช้ lock เพื่อป้องกันการแย่งกันใช้ข้อมูลระหว่าง UI Thread กับ Data Thread
            lock (_plotView.Model.SyncRoot)
            {
                double time = DateTimeAxis.ToDouble(DateTime.Now);

                _measuredSeries.Points.Add(new DataPoint(time, measured));

                if (_measuredSeries.Points.Count > MaxPoints)
                    _measuredSeries.Points.RemoveAt(0);
            }

            // สั่งวาดใหม่ (Thread-Safe update)
            _plotView.InvalidatePlot(true);
        }
        /// <summary>
        /// ล้างกราฟทั้งหมด (เผื่อกดปุ่ม Reset)
        /// </summary>
        public void Clear()
        {
            lock (_plotView.Model.SyncRoot)
            {
                _setpointSeries.Points.Clear();
                _measuredSeries.Points.Clear();
            }
            _plotView.InvalidatePlot(true);
        }
    }
}