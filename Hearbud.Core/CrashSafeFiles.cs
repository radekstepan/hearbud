// v2 port (hearbud-b blueprint, principle L3 "Crash-safe by construction"):
// record to tmp files, then atomic-rename on finalize, plus a disk-space
// preflight so sessions fail fast instead of mid-take. See ADR-0003/0011.

using System;
using System.IO;

namespace Hearbud
{
    /// <summary>Outcome of <see cref="CrashSafeFiles.EvaluateSpace"/>.</summary>
    public sealed record SpaceVerdict(bool CanRecord, string? Warning);

    /// <summary>
    /// Small, hardware-free file helpers for crash-safe recording.
    /// Pure logic is unit-tested; only <see cref="Preflight"/> touches the disk.
    /// </summary>
    public static class CrashSafeFiles
    {
        /// <summary>Suffix appended to the final name while a take is in progress.</summary>
        public const string TmpSuffix = ".tmp";

        /// <summary>Below this many free bytes, refuse to start recording.</summary>
        public const long FailBelowFreeBytes = 20L * 1024 * 1024;

        /// <summary>Below this many free bytes (or 5 min of estimated audio), warn but proceed.</summary>
        public const long WarnBelowFreeBytes = 100L * 1024 * 1024;

        /// <summary>Derives the in-progress tmp path for a final output path.</summary>
        public static string TmpPathFor(string finalPath) => finalPath + TmpSuffix;

        /// <summary>
        /// Returns <paramref name="path"/> if free, else "name (1).ext", "name (2).ext", …
        /// Same non-clobbering rule the engine has always used, now shared and tested.
        /// </summary>
        public static string EnsureUniquePath(string path)
        {
            try
            {
                if (!File.Exists(path)) return path;
                var dir = Path.GetDirectoryName(path)!;
                var name = Path.GetFileNameWithoutExtension(path);
                var ext = Path.GetExtension(path);
                int i = 1;
                string candidate;
                do
                {
                    candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                    i++;
                }
                while (File.Exists(candidate));
                return candidate;
            }
            catch { return path; }
        }

        /// <summary>
        /// Atomically publishes a finished tmp file to its final name.
        /// Re-checks uniqueness at move time (a crash-recovery leftover could collide),
        /// then File.Move (atomic on NTFS when source and dest share a volume).
        /// Returns the path actually published to.
        /// </summary>
        public static string AtomicPublish(string tmpPath, string finalPath)
        {
            var dest = EnsureUniquePath(finalPath);
            File.Move(tmpPath, dest);
            return dest;
        }

        /// <summary>
        /// Estimates combined WAV bytes/sec. Phase 4: all three legs are float32
        /// (one format everywhere), so 12 bytes/frame × rate × channels.
        /// </summary>
        public static long EstimateBytesPerSecond(int sampleRate, int channels)
        {
            if (sampleRate <= 0 || channels <= 0) return 0;
            return (long)sampleRate * channels * 12;
        }

        /// <summary>
        /// Pure preflight rule: fail when nearly full, warn when low or under ~5 min
        /// of headroom for the estimated bitrate. IO-free so it can be unit-tested.
        /// </summary>
        public static SpaceVerdict EvaluateSpace(long availableBytes, long bytesPerSecond)
        {
            if (availableBytes < FailBelowFreeBytes)
                return new SpaceVerdict(false, $"Only {availableBytes / 1024 / 1024} MB free — refusing to record.");

            long warnAt = Math.Max(WarnBelowFreeBytes, bytesPerSecond * 300);
            if (availableBytes < warnAt)
                return new SpaceVerdict(true, $"Low disk space ({availableBytes / 1024 / 1024} MB free).");

            return new SpaceVerdict(true, null);
        }

        /// <summary>
        /// Finds unfinished takes: `*.tmp` files left behind by a crash or kill.
        /// Returns full paths sorted by name (≈ chronological for timestamped
        /// takes). Never throws (fail-open: no sweep).
        /// </summary>
        public static string[] FindOrphanedTmp(string directory)
        {
            try
            {
                if (!Directory.Exists(directory)) return Array.Empty<string>();
                var found = Directory.GetFiles(directory, "*" + TmpSuffix);
                Array.Sort(found, StringComparer.OrdinalIgnoreCase);
                return found;
            }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>
        /// Publishes every orphaned tmp in <paramref name="directory"/> to its
        /// final name (suffix stripped, renumbered on collision). Returns the
        /// paths actually published. Skips (leaves in place) files that fail.
        /// </summary>
        public static string[] RecoverOrphanedTmp(string directory)
        {
            var published = new List<string>();
            foreach (var tmp in FindOrphanedTmp(directory))
            {
                try
                {
                    if (!tmp.EndsWith(TmpSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    var final = tmp.Substring(0, tmp.Length - TmpSuffix.Length);
                    published.Add(AtomicPublish(tmp, final));
                }
                catch { /* leave the orphan for the next sweep */ }
            }
            return published.ToArray();
        }

        /// <summary>
        /// Checks free space on the volume hosting <paramref name="directory"/>.
        /// Fail-open (CanRecord=true) when the volume cannot be queried — a broken
        /// preflight must never block a session worse than no preflight.
        /// </summary>
        public static (SpaceVerdict Verdict, long AvailableBytes) Preflight(string directory, long bytesPerSecond)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(directory));
                if (string.IsNullOrEmpty(root))
                    return (new SpaceVerdict(true, null), -1);

                long available = new DriveInfo(root).AvailableFreeSpace;
                return (EvaluateSpace(available, bytesPerSecond), available);
            }
            catch
            {
                return (new SpaceVerdict(true, null), -1);
            }
        }
    }
}
