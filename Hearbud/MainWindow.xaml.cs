using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls; // ComboBoxItem

using WpfMessageBox = System.Windows.MessageBox;
using WinFormsFolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using H.NotifyIcon;

namespace Hearbud
{
    public partial class MainWindow : Window
    {
    private readonly RecorderEngine _engine = new();
    private readonly System.Timers.Timer _uiTimer = new(100);
    private TaskbarIcon? _trayIcon;

    private AppSettings _settings = AppSettings.Load();
        // Phase 4: the engine owns all WASAPI objects; the UI only holds name→id maps.
        private readonly Dictionary<string, string> _micDict = new();
        private readonly Dictionary<string, string> _spkDict = new();

        private bool _uiReady;

        // Phase 1 (v2 supervise): latest 1 Hz engine health, for tray/meters.
        private HealthReport? _latestHealth;
        private bool _humShown;

        public MainWindow()
        {
            try
            {
                InitializeComponent();

                // Shortcuts
                CommandBindings.Add(new CommandBinding(ApplicationCommands.New, (_, __) => OnStart(null, null)));
                InputBindings.Add(new KeyBinding(ApplicationCommands.New, new KeyGesture(Key.R, ModifierKeys.Control)));
                CommandBindings.Add(new CommandBinding(ApplicationCommands.Stop, (_, __) => OnStop(null, null)));
                InputBindings.Add(new KeyBinding(ApplicationCommands.Stop, new KeyGesture(Key.S, ModifierKeys.Control)));

                // Setup system tray icon
                SetupTrayIcon();

                Loaded += (_, __) =>
                {
                    SafeRefreshDevices();
                    LoadSettingsToUi();

                    // default base name
                    var defBase = $"rec-{DateTime.Now:yyyyMMdd_HHmmss}";
                    if (string.IsNullOrWhiteSpace(BaseNameText.Text))
                        BaseNameText.Text = defBase;

                    _uiReady = true;
                    UpdateGainLabels();

                    // Display version
                    var version = Assembly.GetExecutingAssembly().GetName().Version;
                    if (version != null)
                    {
                        VersionText.Text = $"v{version.Major}.{version.Minor}.{version.Build}";
                    }

                    // Phase 1: recover unfinished takes (*.tmp) left by a crash/kill.
                    // Auto-publishes with unique names — a rare info dialog, never silent.
                    try
                    {
                        var sweepDir = OutputFolderText.Text;
                        if (string.IsNullOrWhiteSpace(sweepDir))
                            sweepDir = _settings.OutputDir;
                        if (string.IsNullOrWhiteSpace(sweepDir))
                            sweepDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Recordings");
                        var recovered = CrashSafeFiles.RecoverOrphanedTmp(sweepDir);
                        if (recovered.Length > 0)
                        {
                            StatusText.Text = $"Recovered {recovered.Length} unfinished take(s)";
                            WpfMessageBox.Show(
                                $"Recovered {recovered.Length} unfinished take(s) to:\n{sweepDir}",
                                "Recovered recordings",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                    }
                    catch { /* the sweep must never break startup */ }

                    // Auto-start live monitoring so meters show immediately
                    TryStartAutoMonitor();

                    // Phase 4: device notifications are engine-owned (it also
                    // auto-recovers capture legs); the UI just refreshes lists.
                    // Engine events arrive on a COM thread — marshal to the UI.
                    _engine.DevicesChanged += (_, __) => Dispatcher.BeginInvoke(() =>
                    {
                        SafeRefreshDevices();
                        UpdateTrayTooltip();
                        if (!_engine.IsRecording) TryStartAutoMonitor();
                    });
                };

                _engine.LevelChanged += OnEngineLevelChanged;
                _engine.Status += OnEngineStatus;
                _engine.EncodingProgress += OnEngineEncodingProgress;
                _engine.Health += OnEngineHealth;

                _uiTimer.Elapsed += (_, __) => Dispatcher.Invoke(UpdateMeters);
                _uiTimer.Start();
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("MainWindow.ctor", ex);
                throw;
            }
        }

        private void OnToggleTheme(object sender, RoutedEventArgs e) { /* hidden; no-op */ }

        private void OnRefreshDevices(object sender, RoutedEventArgs e)
        {
            SafeRefreshDevices();
            TryStartAutoMonitor(); // keep meters alive after device changes
        }

        private void SafeRefreshDevices()
        {
            try
            {
                _micDict.Clear(); _spkDict.Clear();

                string? defMic = null, defSpk = null;
                foreach (var d in RecorderEngine.GetMicrophoneDevices())
                {
                    _micDict[d.Name] = d.Id;
                    if (d.IsDefault) defMic = d.Name;
                }
                foreach (var d in RecorderEngine.GetLoopbackDevices())
                {
                    _spkDict[d.Name] = d.Id;
                    if (d.IsDefault) defSpk = d.Name;
                }

                MicCombo.ItemsSource = _micDict.Keys.ToList();
                SpeakerCombo.ItemsSource = _spkDict.Keys.ToList();

                if (defMic != null && _micDict.ContainsKey(defMic))
                    MicCombo.SelectedItem = defMic;
                if (defSpk != null && _spkDict.ContainsKey(defSpk))
                    SpeakerCombo.SelectedItem = defSpk;

                if (MicCombo.SelectedItem == null && _micDict.Count > 0)
                    MicCombo.SelectedItem = _micDict.Keys.First();
                if (SpeakerCombo.SelectedItem == null && _spkDict.Count > 0)
                    SpeakerCombo.SelectedItem = _spkDict.Keys.First();

                StatusText.Text = "Devices refreshed";
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("SafeRefreshDevices", ex);
                StatusText.Text = "Device refresh failed (see log).";
            }
        }

        private async void TryStartAutoMonitor()
        {
            await ExecuteWithDeviceRetryAsync(async () =>
            {
                var micName = MicCombo.SelectedItem as string;
                var spkName = SpeakerCombo.SelectedItem as string;
                if (spkName == null || micName == null) return;

                _engine.MicGain = MicGain.Value;
                _engine.LoopGain = LoopGain.Value;

                await _engine.MonitorAsync(new RecorderStartOptions
                {
                    LoopbackDeviceId = _spkDict[spkName],
                    MicDeviceId = _micDict[micName]
                });
                StatusText.Text = "Monitoring...";
            });
        }

        private async Task ExecuteWithDeviceRetryAsync(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x88890004)) // AUDCLNT_E_DEVICE_INVALIDATED
            {
                StatusText.Text = "Refreshing devices...";
                SafeRefreshDevices();
                try
                {
                    await action();
                }
                catch (Exception retryEx)
                {
                    CrashLog.LogAndShow("Device retry", retryEx);
                }
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("ExecuteWithDeviceRetry", ex);
            }
        }

        private void OnBrowseFolder(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new WinFormsFolderBrowserDialog
                {
                    InitialDirectory = Directory.Exists(OutputFolderText.Text)
                        ? OutputFolderText.Text
                        : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                    ShowNewFolderButton = true
                };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    OutputFolderText.Text = dlg.SelectedPath;
                }
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("OnBrowseFolder", ex);
            }
        }

        private async void OnStart(object? sender, RoutedEventArgs? e)
        {
            try
            {
                var outDir = OutputFolderText.Text;
                if (string.IsNullOrWhiteSpace(outDir) || !Directory.Exists(outDir))
                {
                    var def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Recordings");
                    Directory.CreateDirectory(def);
                    OutputFolderText.Text = def;
                    outDir = def;
                }

                var baseName = SanitizeFilename(BaseNameText.Text);
                if (string.IsNullOrWhiteSpace(baseName))
                {
                    baseName = $"rec-{DateTime.Now:yyyyMMdd_HHmmss}";
                    BaseNameText.Text = baseName;
                }

                // Validate total path length to avoid Windows MAX_PATH (260) exceptions.
                // We check against 240 to leave room for internal suffixes like -system.wav or -mic.wav.
                var testPath = Path.Combine(outDir, $"{baseName}-system.wav");
                if (testPath.Length > 240)
                {
                    WpfMessageBox.Show(
                        "Output path is too long. Please use a shorter folder path or file name.",
                        "Path Too Long",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                var micName = MicCombo.SelectedItem as string;
                var spkName = SpeakerCombo.SelectedItem as string;

                if (spkName == null)
                {
                    WpfMessageBox.Show("Select an output device to capture.", "Missing device",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (micName == null)
                {
                    WpfMessageBox.Show("Select a microphone.", "Missing device",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string loopId = _spkDict[spkName];

                _engine.MicGain = MicGain.Value;   // meters & mix balance
                _engine.LoopGain = LoopGain.Value; // meters & mix balance

                await _engine.StartAsync(new RecorderStartOptions
                {
                    OutputPath = Path.Combine(outDir, baseName),
                    LoopbackDeviceId = loopId,
                    MicDeviceId = _micDict[micName],
                    Mp3BitrateKbps = GetSelectedBitrateKbps()
                });

                StartBtn.IsEnabled = false;
                StopBtn.IsEnabled  = true;
                StatusText.Text = "Recording...";
                UpdateTrayTooltip();
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("OnStart", ex);
            }
        }

        private async void OnStop(object? sender, RoutedEventArgs? e)
        {
            StopBtn.IsEnabled = false;
            try 
            {
                await _engine.StopAsync(); 
            }
            catch (Exception ex) 
            { 
                CrashLog.LogAndShow("OnStop", ex);
                StartBtn.IsEnabled = true;
                StopBtn.IsEnabled = false;
            }
            UpdateTrayTooltip();
        }

        private static string SanitizeFilename(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string(name.Where(c => !invalid.Contains(c)).ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? "recording" : sanitized.Trim();
        }

        private void OnGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_uiReady) return;
            UpdateGainLabels();
            _engine.MicGain = MicGain.Value;
            _engine.LoopGain = LoopGain.Value;
        }

        private void UpdateGainLabels()
        {
            if (MicGainLabel == null || LoopGainLabel == null) return;
            MicGainLabel.Text  = Dbfs.FormatGain(MicGain.Value);
            LoopGainLabel.Text = Dbfs.FormatGain(LoopGain.Value);
        }

        private volatile float _micPeak;
        private volatile float _sysPeak;
        private DateTime _micClipUntil = DateTime.MinValue;
        private DateTime _sysClipUntil = DateTime.MinValue;

        private void OnEngineLevelChanged(object? sender, LevelChangedEventArgs e)
        {
            if (e.Source == LevelSource.Mic) _micPeak = e.Peak;
            else _sysPeak = e.Peak;

            if (e.Clipped)
            {
                if (e.Source == LevelSource.Mic) _micClipUntil = DateTime.Now.AddMilliseconds(1500);
                else _sysClipUntil = DateTime.Now.AddMilliseconds(1500);
            }
        }

        private void UpdateMeters()
        {
            var mp = Math.Clamp(_micPeak, 0f, 1f);
            var sp = Math.Clamp(_sysPeak, 0f, 1f);

            MicBar.Value = Math.Round(mp * 100, 1);
            SysBar.Value = Math.Round(sp * 100, 1);

            MicDb.Text = $"{Dbfs.ToDbfs(mp),6:0.0} dBFS";
            SysDb.Text = $"{Dbfs.ToDbfs(sp),6:0.0} dBFS";

            MicClip.Foreground = DateTime.Now < _micClipUntil ? (System.Windows.Media.Brush)FindResource("ClipBrush") : (System.Windows.Media.Brush)FindResource("ControlDisabled");
            SysClip.Foreground = DateTime.Now < _sysClipUntil ? (System.Windows.Media.Brush)FindResource("ClipBrush") : (System.Windows.Media.Brush)FindResource("ControlDisabled");
        }

        // Phase 1 (v2 supervise): stash the 1 Hz health report for the tray tooltip
        // and surface mains hum in the status line while it persists.
        private void OnEngineHealth(object? sender, HealthReport e)
        {
            _latestHealth = e;
            Dispatcher.Invoke(() =>
            {
                try
                {
                    if (e.HumDetected)
                    {
                        StatusText.Text = $"Mains hum @ {e.HumFreqHz} Hz — check grounding / Listen loop (Troubleshooting Guide: hum triage)";
                        _humShown = true;
                    }
                    else if (_humShown)
                    {
                        _humShown = false;
                        StatusText.Text = _engine.IsRecording ? "Recording..." : "Monitoring...";
                    }
                    UpdateTrayTooltip();
                }
                catch { }
            });
        }

        private void OnEngineEncodingProgress(object? sender, int percent)
        {
            Dispatcher.Invoke(() =>
            {
                EncodingProgressBar.Value = percent;
                EncodingProgressText.Text = $"{percent}%";
            });
        }

        private void OnEngineStatus(object? sender, EngineStatusEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = e.Message;

                if (e.Kind == EngineStatusKind.Encoding)
                {
                    EncodingProgressBar.Visibility = Visibility.Visible;
                    EncodingProgressText.Visibility = Visibility.Visible;
                    EncodingProgressBar.Value = 0;
                    EncodingProgressText.Text = "0%";
                }
                else if (e.Kind == EngineStatusKind.Stopped)
                {
                    EncodingProgressBar.Visibility = Visibility.Collapsed;
                    EncodingProgressText.Visibility = Visibility.Collapsed;

                    StartBtn.IsEnabled = true;
                    StopBtn.IsEnabled = false;
                    UpdateTrayTooltip();

                    WpfMessageBox.Show(
                        $"Saved files to:\n{Path.GetDirectoryName(e.OutputPathSystem)}",
                        "Files saved",
                        MessageBoxButton.OK, MessageBoxImage.Information);

                    // Open the output folder
                    try
                    {
                        string? directoryPath = Path.GetDirectoryName(e.OutputPathSystem);
                        if (!string.IsNullOrEmpty(directoryPath) && Directory.Exists(directoryPath))
                        {
                            Process.Start(new ProcessStartInfo()
                            {
                                FileName = directoryPath,
                                UseShellExecute = true,
                                Verb = "open"
                            });
                        }
                    }
                    catch (Exception ex) { CrashLog.LogAndShow("OpenOutputFolder", ex); }


                    // Keep monitoring so meters remain live after stop
                    TryStartAutoMonitor();
                }
                else if (e.Kind == EngineStatusKind.Error)
                {
                    WpfMessageBox.Show(e.Message, "Recording Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });
        }

        private void SetupTrayIcon()
        {
            try
            {
                _trayIcon = new TaskbarIcon
                {
                    IconSource = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/hearbud.ico")),
                    ToolTipText = "Hearbud - Audio Recorder"
                };

                // Left-click to show/hide window
                _trayIcon.TrayLeftMouseUp += (s, e) =>
                {
                    if (IsVisible)
                    {
                        Hide();
                    }
                    else
                    {
                        Show();
                        WindowState = WindowState.Normal;
                        Activate();
                    }
                };

                // Right-click context menu
                var contextMenu = new System.Windows.Controls.ContextMenu();
                
                var showItem = new System.Windows.Controls.MenuItem { Header = "Show Window" };
                showItem.Click += (s, e) =>
                {
                    Show();
                    WindowState = WindowState.Normal;
                    Activate();
                };
                contextMenu.Items.Add(showItem);

                var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
                exitItem.Click += (s, e) => System.Windows.Application.Current.Shutdown();
                contextMenu.Items.Add(exitItem);

                _trayIcon.ContextMenu = contextMenu;
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("SetupTrayIcon", ex);
            }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
        }

        /// <summary>
        /// Handles the window closing event. If a recording is in progress, 
        /// prompts the user for confirmation to prevent accidental data loss.
        /// </summary>
        protected override async void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            if (_engine.IsRecording)
            {
                var result = WpfMessageBox.Show(
                    "Recording is in progress. Stop recording and exit?",
                    "Confirm Exit",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }

                // Ensure the engine stops and flushes before the window fully closes
                await _engine.StopAsync();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try { _uiTimer.Stop(); } catch { }
            try { _trayIcon?.Dispose(); } catch { }

            // (Phase 4: device notifications are engine-owned; _engine.Dispose() stops them.)

            try
            {
                // Persist current UI selections
                _settings.MicName = MicCombo.SelectedItem as string ?? _settings.MicName;
                _settings.SpeakerName = SpeakerCombo.SelectedItem as string ?? _settings.SpeakerName;
                _settings.OutputDir = OutputFolderText.Text;
                _settings.MicGain = MicGain.Value;
                _settings.LoopGain = LoopGain.Value;
                _settings.Mp3BitrateKbps = GetSelectedBitrateKbps();
                _settings.Save();
            }
            catch { /* best-effort */ }
            _engine.Dispose();
        }

        private void LoadSettingsToUi()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_settings.OutputDir))
                    OutputFolderText.Text = _settings.OutputDir;
                else
                {
                    var def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Recordings");
                    Directory.CreateDirectory(def);
                    OutputFolderText.Text = def;
                }

                if (!string.IsNullOrWhiteSpace(_settings.MicName) && _micDict.ContainsKey(_settings.MicName))
                    MicCombo.SelectedItem = _settings.MicName;
                if (!string.IsNullOrWhiteSpace(_settings.SpeakerName) && _spkDict.ContainsKey(_settings.SpeakerName))
                    SpeakerCombo.SelectedItem = _settings.SpeakerName;

                MicGain.Value  = _settings.MicGain;
                LoopGain.Value = _settings.LoopGain;

                // Select Output Quality (0 for WAV, else MP3 bitrate)
                var desired = _settings.Mp3BitrateKbps < 0 ? 192 : _settings.Mp3BitrateKbps;
                OutputQualityCombo.SelectedValue = desired;
                
                // If desired value isn't in the list, fall back to 192.
                if (OutputQualityCombo.SelectedItem as ComboBoxItem == null)
                    OutputQualityCombo.SelectedValue = 192;
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("LoadSettingsToUi", ex);
            }
        }

        private int GetSelectedBitrateKbps()
        {
            try
            {
                int kb = 192; // default
                if (OutputQualityCombo.SelectedItem is ComboBoxItem item &&
                    item.Tag is string s &&
                    int.TryParse(s, out var parsedKb))
                {
                    kb = parsedKb;
                }
                else if (OutputQualityCombo.SelectedValue is int kbpsInt)
                {
                    kb = kbpsInt;
                }
                else if (OutputQualityCombo.SelectedValue is string kbpsStr && int.TryParse(kbpsStr, out var kbpsParsed))
                {
                    kb = kbpsParsed;
                }

                if (kb == 0) return 0; // Original (WAV)
                return Math.Clamp(kb, 64, 320);
            }
            catch { }
            return 192;
        }

        // (Phase 4: device notifications moved into the engine — the UI subscribes
        // to RecorderEngine.DevicesChanged in Loaded. Capture legs now auto-rebuild
        // instead of stopping the session. See ADR-0012.)

        /// <summary>
        /// Updates the system tray tooltip to reflect whether recording is active
        /// and which output device is selected.
        /// </summary>
        private void UpdateTrayTooltip()
        {
            if (_trayIcon == null) return;
            var spkName = SpeakerCombo.SelectedItem as string;
            var micName = MicCombo.SelectedItem as string;

            var health = _latestHealth;
            string suffix = health == null ? ""
                : health.HumDetected ? $" \u00b7 hum @{health.HumFreqHz}Hz"
                : health.DroppedBlocks > 0 ? $" \u00b7 {health.DroppedBlocks} dropped"
                : "";

            _trayIcon.ToolTipText = (_engine.IsRecording
                ? $"\u25cf Recording \u2014 {spkName ?? "No device"}"
                : spkName != null
                    ? $"Hearbud \u2014 {spkName}"
                    : "Hearbud \u2014 No device connected") + suffix;
        }
    }
}
