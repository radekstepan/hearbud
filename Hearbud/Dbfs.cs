using System;

namespace Hearbud
{
    public static class Dbfs
    {
        public static string FormatGain(double value)
        {
            if (value <= 0.0) return $" 0.00× ( -inf dB)";
            var db = 20.0 * Math.Log10(value);
            return $"{value,5:0.00}× ({db,6:+0.0;-0.0} dB)";
        }

        public static double ToDbfs(double peakLin)
        {
            if (peakLin <= 1e-6) return -60.0;
            var d = 20.0 * Math.Log10(peakLin);
            return Math.Max(-60.0, d);
        }
    }
}
