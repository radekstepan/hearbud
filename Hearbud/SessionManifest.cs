using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Hearbud
{
    /// <summary>
    /// RecExperts FixupFile equivalent: crash-detectable session sidecar.
    /// HistoryStore logs finished sessions; WavRepair fixes headers heuristically;
    /// this manifest binds one in-progress session to its files so a crash without
    /// Dispose is detectable and repairable on next start.
    /// Sidecar: &lt;base&gt;.hearbud-session.json next to the WAV set. Best-effort.
    /// </summary>
    public sealed class SessionManifest
    {
        public int SchemaVersion { get; set; } = 1;
        public string BaseName { get; set; } = "";
        public DateTime StartedUtc { get; set; }
        public DateTime? FinishUtc { get; set; }
        public bool ClosedCleanly { get; set; }
        public Dictionary<string, string> Paths { get; set; } = new();
        public long MixEnqueued { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public int MixBits { get; set; }
        public bool DiskFull { get; set; }
        public long TotalErrors { get; set; }
        public long SysErrors { get; set; }
        public long MicErrors { get; set; }
        public long MixErrors { get; set; }
        public string? FirstTarget { get; set; }
        public string? FirstMessage { get; set; }
        public List<string> Repaired { get; set; } = new();

        public static string PathFor(string outDir, string baseName)
            => Path.Combine(outDir, $"{baseName}.hearbud-session.json");

        public static void Begin(string outDir, string baseName,
            string sys, string mic, string mix, string mp3,
            int sampleRate, int channels, int mixBits)
        {
            try
            {
                var m = new SessionManifest
                {
                    BaseName = baseName,
                    StartedUtc = DateTime.UtcNow,
                    ClosedCleanly = false,
                    SampleRate = sampleRate,
                    Channels = channels,
                    MixBits = mixBits,
                };
                m.Paths["system"] = sys; m.Paths["mic"] = mic;
                m.Paths["mix"] = mix; m.Paths["mp3"] = mp3;
                WriteAtomic(PathFor(outDir, baseName), m);
            }
            catch { }
        }

        public static void MarkClosed(string outDir, string baseName, long mixEnqueued,
            bool diskFull, long total, long sys, long mic, long mixErr,
            string? firstTarget, string? firstMessage)
        {
            try
            {
                string path = PathFor(outDir, baseName);
                SessionManifest m;
                try
                {
                    string json = File.ReadAllText(path);
                    m = JsonSerializer.Deserialize<SessionManifest>(json) ?? new SessionManifest();
                }
                catch { m = new SessionManifest { BaseName = baseName, StartedUtc = DateTime.UtcNow }; }
                m.FinishUtc = DateTime.UtcNow;
                m.ClosedCleanly = true;
                m.MixEnqueued = mixEnqueued;
                m.DiskFull = diskFull;
                m.TotalErrors = total; m.SysErrors = sys; m.MicErrors = mic; m.MixErrors = mixErr;
                m.FirstTarget = firstTarget; m.FirstMessage = firstMessage;
                WriteAtomic(path, m);
            }
            catch { }
        }

        /// <summary>Manifests left unclosed (crash/abort). Corrupt JSON ignored.</summary>
        public static List<SessionManifest> ScanUnclosed(string? dir)
        {
            var result = new List<SessionManifest>();
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return result;
                foreach (var f in Directory.EnumerateFiles(dir, "*.hearbud-session.json", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var m = JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(f));
                        if (m != null && !m.ClosedCleanly) result.Add(m);
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        /// <summary>Repair listed WAVs that fail IsValidWav; records outcome in manifest.</summary>
        public static void RepairUnclosed(string? dir)
        {
            try
            {
                foreach (var m in ScanUnclosed(dir))
                {
                    var repaired = new List<string>();
                    foreach (var kv in m.Paths)
                    {
                        try
                        {
                            string p = kv.Value;
                            if (p.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                                && File.Exists(p) && !WavRepair.IsValidWav(p)
                                && WavRepair.TryRepair(p))
                                repaired.Add(p);
                        }
                        catch { }
                    }
                    if (repaired.Count > 0)
                    {
                        m.Repaired.AddRange(repaired);
                        try
                        {
                            string? d = Path.GetDirectoryName(repaired[0]);
                            if (!string.IsNullOrEmpty(d)) WriteAtomic(PathFor(d, m.BaseName), m);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static void WriteAtomic(string path, SessionManifest m)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
