// v2 port (hearbud-b blueprint, "hum watchdog"): single-frequency energy
// detectors for mains hum (50/60 Hz + harmonics). Proves whether a buzz is
// electrical (mains bins hot) or downstream DSP. Staged as a tested building
// block; wiring into the live path is Phase 2 (see docs/adr/0007-dsp-staging.md).

using System;

namespace Hearbud
{
    /// <summary>
    /// Goertzel single-frequency energy detector. Feed contiguous samples at the
    /// detector's sample rate, then read <see cref="LevelDb"/> and <see cref="Reset"/>.
    /// Not thread-safe; one instance per analysed stream.
    /// </summary>
    public sealed class Goertzel
    {
        /// <summary>Target frequency in Hz.</summary>
        public readonly int Freq;

        private readonly double _coeff;
        private double _s1, _s2;
        private int _n;

        public Goertzel(int sampleRate, int freq)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            Freq = freq;
            _coeff = 2.0 * Math.Cos(2.0 * Math.PI * freq / sampleRate);
        }

        /// <summary>Number of samples accumulated since the last <see cref="Reset"/>.</summary>
        public int SampleCount => _n;

        public void Push(float s)
        {
            double s0 = s + _coeff * _s1 - _s2;
            _s2 = _s1;
            _s1 = s0;
            _n++;
        }

        /// <summary>Normalized power at the target frequency, in dB (silence ≈ −90 floor).</summary>
        public double LevelDb()
        {
            if (_n == 0) return -90.0;
            double p = (_s1 * _s1 + _s2 * _s2 - _coeff * _s1 * _s2) / ((double)_n * _n);
            return 10.0 * Math.Log10(Math.Max(p, 1e-12));
        }

        public void Reset() { _s1 = _s2 = 0.0; _n = 0; }
    }

    /// <summary>
    /// Mains-hum watchdog: a bank of <see cref="Goertzel"/> detectors at 50/60/100/120 Hz.
    /// Feed it the mix (or mic) stream; <see cref="Poll"/> reports the hottest mains bin.
    /// </summary>
    public sealed class HumWatch
    {
        /// <summary>Bins above this level count as detected hum.</summary>
        public const double DetectAboveDb = -50.0;

        private readonly Goertzel[] _bins;

        public HumWatch(int sampleRate) : this(sampleRate, 50, 60, 100, 120) { }

        public HumWatch(int sampleRate, params int[] freqs)
        {
            _bins = new Goertzel[freqs.Length];
            for (int i = 0; i < freqs.Length; i++)
                _bins[i] = new Goertzel(sampleRate, freqs[i]);
        }

        public void Push(float s)
        {
            foreach (var g in _bins) g.Push(s);
        }

        /// <summary>
        /// Returns (detected, freqHz, levelDb) for the hottest bin, then resets all bins.
        /// </summary>
        public (bool Detected, int FreqHz, double LevelDb) Poll()
        {
            bool detected = false;
            int freq = 0;
            double hottest = double.NegativeInfinity;
            foreach (var g in _bins)
            {
                double lvl = g.LevelDb();
                if (lvl > hottest) { hottest = lvl; freq = g.Freq; }
                if (lvl > DetectAboveDb) detected = true;
                g.Reset();
            }
            return (detected, freq, hottest);
        }
    }
}
