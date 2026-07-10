using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IGRF_Interface_Demo1._1.Models
{
    public class ProcessedData
    {
        public double MagX { get; set; }
        public double MagY { get; set; }
        public double MagZ { get; set; }

        public double ErrorX { get; set; }
        public double ErrorY { get; set; }
        public double ErrorZ { get; set; }

        public double ErrorPerX { get; set; }
        public double ErrorPerY { get; set; }
        public double ErrorPerZ { get; set; }
    }
}
