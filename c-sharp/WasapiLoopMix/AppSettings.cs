using System;
using System.IO;
using System.Text.Json;

namespace WasapiLoopMix
{
    public sealed class AppSettings
    {
        public string MicName { get; set; } = "";
        public string SpeakerName { get; set; } = "";
        public string LoopbackName { get; set; } = "Auto (match speaker)";
        public string OutputDir { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Recordings");
        public string Mp3Quality { get; set; } = "Good (192kbps)";
        public double MicGain { get; set; } = 1.0;
        public double LoopGain { get; set; } = 1.0;
        public bool IncludeMic { get; set; } = true;

        private static string ConfigPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".audiorecorder_config.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null) return s;
                }
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch { /* ignore */ }
        }
    }
}
