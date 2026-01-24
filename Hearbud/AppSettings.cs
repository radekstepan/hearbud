using System;
using System.IO;
using System.Text.Json;

namespace Hearbud
{
    /// <summary>
    /// Represents the application settings, providing methods to load, save, and validate configuration.
    /// </summary>
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

        /// <summary>
        /// Loads settings from the configuration file, validating values to ensure they are within safe ranges.
        /// </summary>
        /// <returns>The loaded <see cref="AppSettings"/> instance, or a new instance if loading fails.</returns>
        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null)
                    {
                        s.Validate();
                        return s;
                    }
                }
            }
            catch { }
            return new AppSettings();
        }

        /// <summary>
        /// Validates setting values and clamps them to valid ranges to prevent crashes or unexpected behavior.
        /// </summary>
        private void Validate()
        {
            MicGain = Math.Clamp(MicGain, 0.0, 10.0);
            LoopGain = Math.Clamp(LoopGain, 0.0, 10.0);
            Mp3BitrateKbps = Mp3BitrateKbps == 0 ? 0 : Math.Clamp(Mp3BitrateKbps, 64, 320);

            if (string.IsNullOrWhiteSpace(OutputDir))
                OutputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Recordings");
        }

        /// <summary>
        /// Saves the current settings to the configuration file.
        /// </summary>
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
