// v2 port (hearbud-b blueprint, "soft-limiter with a knee"): global tanh dulled
// even quiet transients, so the live mix uses this limiter instead (Phase 2):
// bit-transparent below the 0.8 knee, smoothly ceilinged to ±1 above it.
// See ADR-0007 (staging) and ADR-0010 (wiring).

using System;

namespace Hearbud
{
    /// <summary>
    /// Kneed soft-limiter: bit-transparent below <see cref="Knee"/>, smoothly
    /// ceilinged to ±1 above it. Pure and allocation-free.
    /// </summary>
    public static class SoftLimiter
    {
        /// <summary>Signals below this magnitude pass through untouched.</summary>
        public const float Knee = 0.8f;

        public static float Limit(float x)
        {
            // Match tanh semantics for non-finite input (never emit NaN for ±∞).
            if (float.IsPositiveInfinity(x)) return 1f;
            if (float.IsNegativeInfinity(x)) return -1f;
            if (float.IsNaN(x)) return float.NaN;

            float a = MathF.Abs(x);
            if (a < Knee) return x;
            float over = a - Knee;
            float limited = Knee + over / (1f + over * 2.2f);
            return MathF.CopySign(MathF.Min(limited, 1f), x);
        }
    }
}
