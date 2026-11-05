using System;
using Avalonia;

namespace Hearbud
{
    internal sealed class Program
    {
        // Avalonia bootstrap: classic desktop lifetime (single MainWindow).
        [STAThread]
        public static void Main(string[] args)
        {
            // Backend selection: PortAudio capture on macOS, WASAPI elsewhere.
            // (The MP3 encoder inside the engine already switches by OS itself.)
            if (OperatingSystem.IsMacOS())
                AudioCapture.Default = MacAudioCapture.Factory;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();
    }
}
