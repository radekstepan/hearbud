using System;
using System.Windows;
using System.Windows.Threading;

namespace Hearbud
{
    /// <summary>
    /// GAP-09 #4: tiny always-on-top companion window shown while recording.
    /// It owns no engine logic: MainWindow wires polling delegates + actions,
    /// and a 1 s timer refreshes time / level bars / pause label. Closed (or
    /// hidden) together with the main window; never touches engine/pause/limits.
    /// </summary>
    public partial class MiniBar : Window
    {
        private readonly Func<TimeSpan> _getElapsed;
        private readonly Func<(float mic, float sys, bool paused)> _getState;
        private readonly Action _onPause;
        private readonly Action _onStop;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

        public MiniBar(
            Func<TimeSpan> getElapsed,
            Func<(float mic, float sys, bool paused)> getState,
            Action onPause,
            Action onStop)
        {
            _getElapsed = getElapsed;
            _getState = getState;
            _onPause = onPause;
            _onStop = onStop;

            InitializeComponent();

            _timer.Tick += (_, __) => Refresh();
            _timer.Start();
            Refresh();
        }

        private void Refresh()
        {
            try
            {
                var elapsed = _getElapsed();
                TimeText.Text = elapsed.TotalHours >= 1
                    ? elapsed.ToString(@"h\:mm\:ss")
                    : elapsed.ToString(@"mm\:ss");

                var (mic, sys, paused) = _getState();
                MicBar.Value = Math.Clamp(mic, 0f, 1f) * 100;
                SysBar.Value = Math.Clamp(sys, 0f, 1f) * 100;
                PauseBtn.Content = paused ? "Resume" : "Pause";
            }
            catch { /* best-effort: window must never crash the session */ }
        }

        private void OnPauseClick(object sender, RoutedEventArgs e)
        {
            try { _onPause(); } catch { }
        }

        private void OnStopClick(object sender, RoutedEventArgs e)
        {
            try { _onStop(); } catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _timer.Stop(); } catch { }
            base.OnClosed(e);
        }
    }
}
