// A single ~1 Hz health snapshot replaces scattered per-block log lines.
// See docs/adr/0004-health-supervision.md and docs/TROUBLESHOOTING.md (hum triage).

using System;
using System.Diagnostics;

namespace Hearbud
{
    /// <summary>
    /// Point-in-time engine health, raised on <see cref="RecorderEngine.Health"/>
    /// at ~1 Hz while monitoring/recording. Cheap to construct; safe to log or bind to UI.
    /// </summary>
    /// <param name="DroppedBlocks">Audio blocks dropped because the disk queue was full.</param>
    /// <param name="MicUnderrunBlocks">Loopback blocks that found the mic ring empty (zero-filled).</param>
    /// <param name="MicBacklogSec">Current mic ring backlog in seconds (~0.1 = healthy).</param>
    /// <param name="MicBacklogMaxSec">Peak mic ring backlog since recording started.</param>
    /// <param name="LoopBlocks">Total loopback blocks processed this session.</param>
    /// <param name="DriftMs">
    /// Wall-clock (QPC) elapsed minus loopback-stream elapsed, in ms.
    /// Positive = stream is lagging the wall clock. Near 0 = healthy.
    /// </param>
    /// <param name="TimestampUtc">When the snapshot was taken.</param>
    /// <param name="HumDetected">True when a mains-hum bin exceeded the threshold in the last window.</param>
    /// <param name="HumFreqHz">Hottest mains bin in Hz (meaningful when <paramref name="HumDetected"/>).</param>
    public sealed record HealthReport(
        long DroppedBlocks,
        long MicUnderrunBlocks,
        double MicBacklogSec,
        double MicBacklogMaxSec,
        long LoopBlocks,
        double DriftMs,
        DateTime TimestampUtc,
        bool HumDetected = false,
        int HumFreqHz = 0);

    /// <summary>
    /// Pure helpers for health computation. Kept static so they are unit-testable
    /// without audio hardware.
    /// </summary>
    public static class EngineHealth
    {
        /// <summary>Minimum interval between <see cref="HealthReport"/> events, in milliseconds.</summary>
        public const double IntervalMs = 1000.0;

        /// <summary>
        /// Compares QPC wall-clock elapsed against loopback stream elapsed.
        /// v2 blueprint: QPC is the referee between independent device crystals;
        /// this is the first timestamp-based signal (the full drift PLL is future work,
        /// see docs/adr/0005-qpc-timestamps-before-pll.md).
        /// </summary>
        /// <param name="qpcStart">Stopwatch tick captured when the session clock started.</param>
        /// <param name="qpcNow">Current Stopwatch tick.</param>
        /// <param name="streamFrames">Loopback frames delivered since <paramref name="qpcStart"/>.</param>
        /// <param name="sampleRate">Loopback sample rate in Hz.</param>
        /// <returns>Wall-minus-stream skew in milliseconds. 0 when inputs are degenerate.</returns>
        public static double ComputeDriftMs(long qpcStart, long qpcNow, long streamFrames, int sampleRate)
        {
            if (sampleRate <= 0 || streamFrames < 0 || qpcNow < qpcStart)
                return 0.0;

            double wallSec = (double)(qpcNow - qpcStart) / Stopwatch.Frequency;
            double streamSec = (double)streamFrames / sampleRate;
            return (wallSec - streamSec) * 1000.0;
        }
    }
}
