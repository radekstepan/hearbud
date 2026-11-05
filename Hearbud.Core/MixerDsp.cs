// Phase 4 (v2 port, L2): the mixer-thread DSP as pure, tested functions.
// Gains → per-leg DC block → average → kneed limit, with per-leg post-gain
// meters and the hum tap observing exactly what is recorded. All state lives
// in the caller-owned DcBlocker/HumWatch instances (mixer thread only).

using System;

namespace Hearbud
{
    public static class MixerDsp
    {
        public static void ProcessMixBlock(
            float[] sys, float[] mic, float[] mix, int count, int channels,
            double sysGain, double micGain,
            DcBlocker dcSys, DcBlocker dcMic, HumWatch? hum,
            out float sysRms, out float sysPeak, out bool sysClipped,
            out float micRms, out float micPeak, out bool micClipped)
        {
            double sysSum = 0.0, micSum = 0.0;
            float sysPk = 0f, micPk = 0f;
            bool sysCl = false, micCl = false;
            for (int i = 0; i < count; i++)
            {
                int ch = i % channels;
                float s = (float)(sys[i] * sysGain);
                float m = (float)(mic[i] * micGain);
                float sa = MathF.Abs(s), ma = MathF.Abs(m);
                if (sa > sysPk) sysPk = sa;
                if (ma > micPk) micPk = ma;
                sysSum += (double)sa * sa;
                micSum += (double)ma * ma;
                if (sa > 1f) sysCl = true;
                if (ma > 1f) micCl = true;
                s = dcSys.Filter(s, ch);
                m = dcMic.Filter(m, ch);
                float a = SoftLimiter.Limit((s + m) * 0.5f);
                mix[i] = a;
                hum?.Push(a);
            }
            sysRms = count > 0 ? (float)Math.Sqrt(sysSum / count) : 0f;
            sysPeak = sysPk;
            sysClipped = sysCl;
            micRms = count > 0 ? (float)Math.Sqrt(micSum / count) : 0f;
            micPeak = micPk;
            micClipped = micCl;
        }
    }
}
