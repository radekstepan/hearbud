using System;
using System.IO;
using System.Threading.Tasks;

namespace WasapiLoopMix
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(System.Windows.StartupEventArgs e)
        {
            // Global crash handlers
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                CrashLog.LogAndShow("AppDomain.CurrentDomain.UnhandledException", args.ExceptionObject as Exception);

            DispatcherUnhandledException += (_, args) =>
            {
                CrashLog.LogAndShow("Application.DispatcherUnhandledException", args.Exception);
                args.Handled = true; // keep app alive if possible
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                CrashLog.LogAndShow("TaskScheduler.UnobservedTaskException", args.Exception);
                args.SetObserved();
            };

            base.OnStartup(e);
        }
    }

    internal static class CrashLog
    {
        private static readonly string LogDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WasapiLoopMix");
        private static readonly string LogPath = Path.Combine(LogDir, "logs.txt");

        public static void LogAndShow(string where, Exception? ex)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}\n{ex}\n\n");
            }
            catch { /* ignore */ }

            var msg = ex?.ToString() ?? "(null exception)";
            try
            {
                System.Windows.MessageBox.Show(
                    $"A fatal error occurred in {where}.\n\n{msg}\n\n" +
                    $"A log was written to:\n{LogPath}",
                    "WasapiLoopMix – Crash",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            catch { /* ignore */ }
        }
    }
}
