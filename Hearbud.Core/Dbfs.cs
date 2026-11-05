using System;

namespace Hearbud
{
    public static class Dbfs
    {
        /// <summary>
        /// Formats a linear gain value as a string with its decibel equivalent.
        /// </summary>
        /// <param name="value">The linear gain value.</param>
        /// <returns>A formatted string representing the gain.</returns>
        public static string FormatGain(double value)
        {
            if (value <= 0.0) return "0.00× (-∞ dB)";
            var db = 20.0 * Math.Log10(value);
            return $"{value:0.00}× ({db:+0.0;-0.0} dB)";
        }

        public static double ToDbfs(double peakLin)
        {
            if (peakLin <= 1e-6) return -60.0;
            var d = 20.0 * Math.Log10(peakLin);
            return Math.Max(-60.0, d);
        }
    }
}
