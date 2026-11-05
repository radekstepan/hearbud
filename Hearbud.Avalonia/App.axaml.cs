using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Hearbud
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // Global crash handlers. Avalonia has no DispatcherUnhandledException
            // equivalent, so UI boundaries are wrapped in try/catch instead
            // (see MainWindow ctor) and CrashLog degrades to log-only when no
            // window exists yet.
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                CrashLog.LogAndShow("AppDomain.CurrentDomain.UnhandledException", args.ExceptionObject as Exception);

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                CrashLog.LogAndShow("TaskScheduler.UnobservedTaskException", args.Exception);
                args.SetObserved();
            };

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = new MainWindow();

            base.OnFrameworkInitializationCompleted();
        }
    }

    internal static class CrashLog
    {
        private static readonly string LogDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hearbud");
        private static readonly string LogPath = Path.Combine(LogDir, "logs.txt");

        // File-only logging for background paths (e.g. auto-monitor) where a
        // modal crash dialog would be wrong; the caller shows status instead.
        public static void Log(string where, Exception? ex)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}\n{ex}\n\n");
            }
            catch { /* ignore */ }
        }

        public static void LogAndShow(string where, Exception? ex)
        {
            Log(where, ex);

            // Unlike WPF's blocking MessageBox, the Avalonia dialog is async, so
            // this stays fire-and-forget; when no window exists it is log-only.
            try
            {
                var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (owner == null) return;
                var msg = ex?.ToString() ?? "(null exception)";
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await MessageDialog.ShowAsync(owner,
                            $"A fatal error occurred in {where}.\n\n{msg}\n\n" +
                            $"A log was written to:\n{LogPath}",
                            "Hearbud - Crash");
                    }
                    catch { /* ignore */ }
                });
            }
            catch { /* ignore */ }
        }
    }
}
