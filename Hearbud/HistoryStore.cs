using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Hearbud
{
    /// <summary>
    /// GAP-09 #8: one finished recording session (mix + optional MP3/stems).
    /// Persisted as JSON by <see cref="HistoryStore"/>.
    /// </summary>
    public sealed class HistoryEntry
    {
        public DateTime Timestamp { get; set; }
        public string BaseName { get; set; } = "";
        public string MixPath { get; set; } = "";
        public string Mp3Path { get; set; } = "";
        public string SystemPath { get; set; } = "";
        public string MicPath { get; set; } = "";
        /// <summary>Wall-clock session length in seconds (engine LastRecordElapsed at stop).</summary>
        public double DurationSeconds { get; set; }
        /// <summary>Mix payload bytes enqueued (engine TotalMixBytesWritten at stop; WAV header excluded).</summary>
        public long SizeBytes { get; set; }

        public override string ToString()
        {
            string dur = TimeSpan.FromSeconds(Math.Max(0, DurationSeconds)).ToString(@"m\:ss", CultureInfo.InvariantCulture);
            return $"{Timestamp:yyyy-MM-dd HH:mm}  {BaseName}  ({dur}, {FormatSize(SizeBytes)})";
        }

        internal static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            double mb = bytes / (1024.0 * 1024.0);
            return mb < 1024 ? $"{mb:0.0} MB" : $"{mb / 1024:0.00} GB";
        }
    }

    /// <summary>
    /// GAP-09 #8: append-only JSON session history (cap <see cref="MaxEntries"/>),
    /// stored beside the config at %UserProfile%\.hearbud_history.json.
    /// All IO is best-effort; failures leave an empty/in-memory list. No new packages.
    /// </summary>
    public sealed class HistoryStore
    {
        public const int MaxEntries = 50;

        private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".hearbud_history.json");

        private readonly string _path;

        public List<HistoryEntry> Entries { get; } = new();

        public HistoryStore(string? path = null)
        {
            _path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
            Load();
        }

        public void Add(HistoryEntry entry)
        {
            try
            {
                Entries.Insert(0, entry);
                while (Entries.Count > MaxEntries)
                    Entries.RemoveAt(Entries.Count - 1);
                Save();
            }
            catch { /* best-effort */ }
        }

        public bool Remove(HistoryEntry entry)
        {
            try
            {
                bool removed = Entries.Remove(entry);
                if (removed) Save();
                return removed;
            }
            catch { return false; }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var list = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_path));
                if (list == null) return;
                foreach (var e in list)
                {
                    if (e == null) continue;
                    Entries.Add(e);
                    if (Entries.Count >= MaxEntries) break;
                }
            }
            catch { /* best-effort: start empty */ }
        }

        private void Save()
        {
            try
            {
                File.WriteAllText(_path,
                    JsonSerializer.Serialize(Entries, IndentedOptions));
            }
            catch { /* best-effort */ }
        }
    }
}
