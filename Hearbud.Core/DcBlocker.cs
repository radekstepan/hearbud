// Phase 2 (v2 port, L2 DSP): 1-pole DC blocker (~8 Hz high-pass at 48 kHz).
// Kills DC offset + subsonic rumble before the mix; keeps voice untouched.
// Applied to the MIX legs only — the raw sys/mic recordings stay bit-raw.
// Not thread-safe: the engine owns one instance per signal per thread.

using System;

namespace Hearbud
{
    public sealed class DcBlocker
    {
        private readonly float[] _prevIn;
        private readonly float[] _prevOut;

        public DcBlocker(int channels)
        {
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
            _prevIn = new float[channels];
            _prevOut = new float[channels];
        }

        public int Channels => _prevIn.Length;

        public float Filter(float x, int channel)
        {
            float y = x - _prevIn[channel] + 0.999f * _prevOut[channel];
            _prevIn[channel] = x;
            _prevOut[channel] = y;
            return y;
        }

        public void Reset()
        {
            Array.Clear(_prevIn, 0, _prevIn.Length);
            Array.Clear(_prevOut, 0, _prevOut.Length);
        }
    }
}
