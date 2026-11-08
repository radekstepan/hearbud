using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Hearbud
{
    /// <summary>
    /// RecExperts parity (FileMuxer::DealFfmpegMuxError): pure per-write error
    /// classification + high-water rule. Cold path only; allocation-free hot path.
    /// </summary>
    public static class WriterHealth
    {
        public const int HrErrorDiskFull = unchecked((int)0x80070070); // ERROR_DISK_FULL (112)
        public const int HrErrorHandleDiskFull = unchecked((int)0x80070027); // ERROR_HANDLE_DISK_FULL (39)

        /// <summary>Cold path only (called from catch). Walks inner exceptions.</summary>
        public static bool IsDiskFull(Exception? ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                int code = e.HResult & 0xFFFF;
                if (code == 112 || code == 39) return true;
                if (e is IOException && e.Message is string m &&
                    (m.Contains("not enough space", StringComparison.OrdinalIgnoreCase)
                  || m.Contains("disk full", StringComparison.OrdinalIgnoreCase))) return true;
            }
            return false;
        }

        /// <summary>Pure occupancy rule: warn at &gt;=75% capacity.</summary>
        public static bool ShouldRaiseHighWater(int count, int capacity)
            => capacity > 0 && count >= capacity - capacity / 4;
    }

    /// <summary>
    /// Sticky per-target write-error counters + first-error capture.
    /// All mutations happen on the single writer thread except Reset (session start).
    /// </summary>
    public sealed class WriterFaultTracker
    {
        public long SysErrors { get; private set; }
        public long MicErrors { get; private set; }
        public long MixErrors { get; private set; }
        public long TotalErrors => SysErrors + MicErrors + MixErrors;
        public Exception? FirstError { get; private set; }
        public AudioFileTarget FirstTarget { get; private set; }
        public long FirstErrorTick { get; private set; }
        public bool DiskFullLatched { get; private set; }
        public long DiskFullTick { get; private set; }

        /// <returns>True when this error latched disk-full.</returns>
        public bool Record(AudioFileTarget target, Exception ex)
        {
            switch (target)
            {
                case AudioFileTarget.System: SysErrors++; break;
                case AudioFileTarget.Mic: MicErrors++; break;
                default: MixErrors++; break;
            }
            if (FirstError == null)
            {
                FirstError = ex;
                FirstTarget = target;
                FirstErrorTick = Stopwatch.GetTimestamp();
            }
            if (!DiskFullLatched && WriterHealth.IsDiskFull(ex))
            {
                DiskFullLatched = true;
                DiskFullTick = Stopwatch.GetTimestamp();
                return true;
            }
            return false;
        }

        public void Reset()
        {
            SysErrors = MicErrors = MixErrors = 0;
            FirstError = null;
            FirstErrorTick = 0;
            DiskFullLatched = false;
            DiskFullTick = 0;
        }
    }
}
