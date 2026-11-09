using System;
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
        /// <summary>
        /// Reports a fatal error without writing a log file. Diagnostics are
        /// routed to the debugger only; the dialog is the user-visible record.
        /// </summary>
        public static void LogAndShow(string where, Exception? ex)
        {
            string detail = ex?.ToString() ?? "(null exception)";
            LogOnly(where, detail);
            try
            {
                System.Windows.MessageBox.Show(
                    $"A fatal error occurred in {where}.\n\n{detail}",
                    "Hearbud - Crash",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// Writes diagnostic output to the debugger only. Kept as a single
        /// no-file sink for the existing diagnostic call sites.
        /// </summary>
        public static void LogOnly(string where, string detail)
        {
            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}\n{detail}");
        }
    }
}
