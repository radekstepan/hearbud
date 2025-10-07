using System;
using System.IO;
using System.Text.Json;

namespace Hearbud
{
    public sealed class AppSettings
    {
        public string MicName { get; set; } = "";
        public string SpeakerName { get; set; } = "";
        public string LoopbackName { get; set; } = "Auto (match speaker)";
        public string OutputDir { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Recordings");

        // Legacy display string kept for backward compat.
        // When loading an old config, this is used to populate Mp3BitrateKbps.
        // It is not actively used or saved by new versions.
        public string? Mp3Quality { get; set; }

        // If 0, means "Original (WAV)". Otherwise, it's the MP3 bitrate.
        public int Mp3BitrateKbps { get; set; } = 192;

        public double MicGain { get; set; } = 1.0;
        public double LoopGain { get; set; } = 1.0;
        public bool IncludeMic { get; set; } = true;

        private static string ConfigPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hearbud_config.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);

                    bool bitratePropertyExists;
                    using (var doc = JsonDocument.Parse(json))
                    {
                        bitratePropertyExists = doc.RootElement.TryGetProperty(nameof(Mp3BitrateKbps), out _);
                    }

                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null)
                    {
                        if (!bitratePropertyExists && !string.IsNullOrWhiteSpace(s.Mp3Quality))
                        {
                            var digits = System.Text.RegularExpressions.Regex.Match(s.Mp3Quality, "(\\d+)");
                            if (digits.Success && int.TryParse(digits.Value, out var kb))
                                s.Mp3BitrateKbps = Math.Clamp(kb, 64, 320);
                            else
                                s.Mp3BitrateKbps = 192;
                        }
                        return s;
                    }
                }
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                // To avoid confusion with legacy configs, we null out the old field.
                this.Mp3Quality = null;
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch { /* ignore */ }
        }
    }
}
