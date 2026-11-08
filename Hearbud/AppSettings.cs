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
        public bool IncludeSystem { get; set; } = true;
        public bool WriteStems { get; set; } = true;

        // GAP-09 auto-stop / auto-split limits. 0 = off.
        public double AutoStopMinutes { get; set; } = 0;
        public double AutoStopSizeMb { get; set; } = 0;
        public bool SplitInsteadOfStop { get; set; } = false;

        // GAP-09 #6: in-app remappable hotkeys (window-focused scope only;
        // system-wide RegisterHotKey is phase 2). Canonical "Ctrl+R" form via
        // KeyGestureConverter round-trip; invalid values fall back to defaults.
        public const string DefaultStartHotkey = "Ctrl+R";
        public const string DefaultPauseHotkey = "Ctrl+P";
        public const string DefaultStopHotkey = "Ctrl+S";

        public string StartHotkey { get; set; } = DefaultStartHotkey;
        public string PauseHotkey { get; set; } = DefaultPauseHotkey;
        public string StopHotkey { get; set; } = DefaultStopHotkey;

        /// <summary>
        /// Normalizes a hotkey string through KeyGestureConverter. Returns false
        /// when the input is null/empty or does not parse; normalized holds the
        /// canonical display string (e.g. "Ctrl+R") on success. Never throws
        /// for bad input (any converter failure yields false and the caller
        /// falls back to the default).
        /// </summary>
        public static bool TryNormalizeHotkey(string? input, out string normalized)
        {
            normalized = "";
            try
            {
                if (string.IsNullOrWhiteSpace(input))
                    return false;
                var conv = new System.Windows.Input.KeyGestureConverter();
                if (conv.ConvertFromString(input.Trim()) is not System.Windows.Input.KeyGesture gesture)
                    return false;
                var s = conv.ConvertToString(gesture);
                if (string.IsNullOrWhiteSpace(s))
                    return false;
                normalized = s;
                return true;
            }
            catch
            {
                return false;
            }
        }

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

            if (double.IsNaN(AutoStopMinutes) || AutoStopMinutes < 0) AutoStopMinutes = 0;
            else AutoStopMinutes = Math.Min(AutoStopMinutes, 24 * 60);
            if (double.IsNaN(AutoStopSizeMb) || AutoStopSizeMb < 0) AutoStopSizeMb = 0;
            else AutoStopSizeMb = Math.Min(AutoStopSizeMb, 100_000);

            if (string.IsNullOrWhiteSpace(OutputDir))
                OutputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Recordings");

            if (!TryNormalizeHotkey(StartHotkey, out var start))
                StartHotkey = DefaultStartHotkey;
            else
                StartHotkey = start;
            if (!TryNormalizeHotkey(PauseHotkey, out var pause))
                PauseHotkey = DefaultPauseHotkey;
            else
                PauseHotkey = pause;
            if (!TryNormalizeHotkey(StopHotkey, out var stop))
                StopHotkey = DefaultStopHotkey;
            else
                StopHotkey = stop;
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
