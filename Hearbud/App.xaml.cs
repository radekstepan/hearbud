using System;
using System.IO;
using System.Threading.Tasks;

namespace Hearbud
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
                
                // Only keep alive for non-critical exceptions
                if (args.Exception is NotImplementedException || 
                    args.Exception is InvalidOperationException)
                {
                    args.Handled = true;
                }
                else
                {
                    // For serious exceptions (NullRef, AccessViolation, etc.), let app terminate
                    args.Handled = false;
                }
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
        // Primary location: the same folder recordings go to (session logs live
        // alongside). Falls back to LocalAppData when unset/unwritable so a
        // crash report is never lost because the output folder is bad.
        private static readonly string FallbackDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hearbud");

        private static string? _logDirOverride;

        private static string DefaultDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Recordings");

        private static string LogDir => _logDirOverride ?? DefaultDir;
        private static string LogPath => Path.Combine(LogDir, "logs.txt");

        /// <summary>
        /// Points the log at the current output folder. Null/empty resets to
        /// the default output folder. Never throws.
        /// </summary>
        public static void SetLogDirectory(string? dir)
        {
            try { _logDirOverride = string.IsNullOrWhiteSpace(dir) ? null : dir; }
            catch { /* ignore */ }
        }

        public static void LogAndShow(string where, Exception? ex)
        {
            string detail = ex?.ToString() ?? "(null exception)";
            string writtenTo = LogOnly(where, detail);
            try
            {
                System.Windows.MessageBox.Show(
                    $"A fatal error occurred in {where}.\n\n{detail}\n\n" +
                    $"A log was written to:\n{(string.IsNullOrEmpty(writtenTo) ? LogPath : writtenTo)}",
                    "Hearbud - Crash",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// Appends a line to the crash log without showing any UI. Used by
        /// low-volume diagnostics (env dump, 1 Hz lag spikes, close summary).
        /// </summary>
        /// <returns>Path actually written, or "" when both locations failed.</returns>
        public static string LogOnly(string where, string detail)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}\n{detail}\n\n";
            try
            {
                Directory.CreateDirectory(LogDir);
                string path = LogPath;
                File.AppendAllText(path, line);
                return path;
            }
            catch { /* fall through to fallback */ }
            try
            {
                Directory.CreateDirectory(FallbackDir);
                string path = Path.Combine(FallbackDir, "logs.txt");
                File.AppendAllText(path, line);
                return path;
            }
            catch { return ""; }
        }
    }
}
