using System;
using One_Sgp4;

namespace IGRF_Interface.Services
{
    public class SatelliteResult
    {
        public double Lat { get; set; }
        public double Lon { get; set; }
        public double Alt { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }

    }
    // Helper Class for ComboBox
    public class SatelliteInfo
    {
        public string Name { get; set; }
        public int ID { get; set; }
        public string Line1 { get; set; }
        public string Line2 { get; set; }
        public override string ToString() => Name;
    }

    public class SatelliteService
    {
        private Tle _currentTle;

        public void SetTLE(string line1, string line2, string line3)
        {
            // Wrapper สำหรับ Parse TLE
            _currentTle = ParserTLE.parseTle(line2, line3, line1);
        }

        public SatelliteResult CalculatePosition(DateTime time)
        {
            if (_currentTle == null) return new SatelliteResult();

            EpochTime epoch = new EpochTime(time);
            var sgp4Data = SatFunctions.getSatPositionAtTime(_currentTle, epoch, Sgp4.wgsConstant.WGS_84);
            var subPoint = SatFunctions.calcSatSubPoint(epoch, sgp4Data, Sgp4.wgsConstant.WGS_84);

            return new SatelliteResult
            {
                Lat = subPoint.getLatitude(),
                Lon = subPoint.getLongitude(),
                Alt = subPoint.getHeight(),
                X = sgp4Data.getX(),
                Y = sgp4Data.getY(),
                Z = sgp4Data.getZ(),
            };
        }
    }
}