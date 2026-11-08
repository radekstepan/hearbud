using CSCore.CoreAudioAPI;
using CSCore.Win32; // PropertyKey
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls; // ComboBoxItem
using System.Windows.Threading;

using WpfMessageBox = System.Windows.MessageBox;
using WinFormsFolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using H.NotifyIcon;

namespace Hearbud
{
    public partial class MainWindow : Window, IMMNotificationClient, IDisposable
    {
    private readonly RecorderEngine _engine = new();
    private readonly System.Timers.Timer _uiTimer = new(100);
    private TaskbarIcon? _trayIcon;
    private MMDeviceEnumerator? _notifEnumerator;

    // GAP-09 auto-stop / auto-split state. Polled on the UI thread once per
    // second while recording. Timer stops on Stopped status (re-entrancy guard:
    // _autoActionRunning ensures one trigger at a time).
    private readonly DispatcherTimer _limitTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private double _limitMinutes;
    private long _limitSizeBytes;
    private bool _splitMode;
    private string _splitOutDir = "";
    private string _splitBaseName = "";
    private int _splitIndex = 1;
    private bool _autoActionRunning;
    private bool _suppressStopUi;

    // GAP-09 #4: pre-record countdown + mini-bar + tray states.
    private CancellationTokenSource? _countdownCts;
    private MiniBar? _miniBar;
    private System.Windows.Controls.MenuItem? _trayPauseItem;
    private System.Windows.Controls.MenuItem? _trayStopItem;
    private string? _lastAutoReason;

    // GAP-09 #8: session history (JSON beside config) + startup repair scan.
    private HistoryStore _history = new();

    private AppSettings _settings = AppSettings.Load();
        private readonly Dictionary<string, MMDevice> _micDict = new();
        private readonly Dictionary<string, MMDevice> _spkDict = new();

        private bool _uiReady;

    private bool _disposed;

        public MainWindow()
        {
            try
            {
                InitializeComponent();

                // GAP-09 #6: remappable in-app hotkeys (window-focused scope only;
                // no RegisterHotKey/HwndSource). Command bindings are static;
                // gestures are rebuilt from settings by RebuildInputBindings().
                CommandBindings.Add(new CommandBinding(ApplicationCommands.New, (_, __) => OnStart(null, null)));
                CommandBindings.Add(new CommandBinding(ApplicationCommands.Stop, (_, __) => OnStop(null, null)));
                CommandBindings.Add(new CommandBinding(MediaCommands.Pause, (_, __) => OnPauseResume(null, null)));
                RebuildInputBindings();

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

                    // Auto-start live monitoring so meters show immediately
                    TryStartAutoMonitor();

                    // GAP-09 #8: session history list + startup repair scan
                    // for unfinalized WAVs (crash/abort leftovers).
                    RefreshHistoryList();
                    RunStartupRepairScan();

                    // Register for Windows audio device notifications (plug/unplug).
                    // Must be done after the window handle exists (i.e. inside Loaded).
                    _notifEnumerator = new MMDeviceEnumerator();
                    _notifEnumerator.RegisterEndpointNotificationCallback(this);
                };

                _engine.LevelChanged += OnEngineLevelChanged;
                _engine.Status += OnEngineStatus;
                _engine.EncodingProgress += OnEngineEncodingProgress;

                _uiTimer.Elapsed += (_, __) => Dispatcher.Invoke(UpdateMeters);
                _uiTimer.Start();

                _limitTimer.Tick += OnLimitTimerTick;
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

                using var enumerator = new MMDeviceEnumerator();

                foreach (var dev in enumerator.EnumAudioEndpoints(DataFlow.Capture, DeviceState.Active))
                    _micDict[dev.FriendlyName] = dev;

                foreach (var dev in enumerator.EnumAudioEndpoints(DataFlow.Render, DeviceState.Active))
                {
                    _spkDict[dev.FriendlyName] = dev;
                }

                MicCombo.ItemsSource = _micDict.Keys.ToList();
                SpeakerCombo.ItemsSource = _spkDict.Keys.ToList();

                try
                {
                    using var e2 = new MMDeviceEnumerator();
                    var defIn = e2.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    if (defIn != null && _micDict.ContainsKey(defIn.FriendlyName))
                        MicCombo.SelectedItem = defIn.FriendlyName;
                }
                catch { }

                try
                {
                    using var e3 = new MMDeviceEnumerator();
                    var defOut = e3.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (defOut != null && _spkDict.ContainsKey(defOut.FriendlyName))
                        SpeakerCombo.SelectedItem = defOut.FriendlyName;
                }
                catch { }

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
                bool wantMic = IncludeMicCheck.IsChecked ?? true;
                bool wantSys = RecordSysCheck.IsChecked ?? true;
                if (!wantMic && !wantSys) return;
                if (wantMic && micName == null) return;
                if (wantSys && spkName == null) return;

                _engine.MicGain = MicGain.Value;
                _engine.LoopGain = LoopGain.Value;

                await _engine.MonitorAsync(new RecorderStartOptions
                {
                    LoopbackDeviceId = wantSys && spkName != null ? _spkDict[spkName].DeviceID : null,
                    MicDeviceId = wantMic && micName != null ? _micDict[micName].DeviceID : null
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
            catch (CoreAudioAPIException ex) when (ex.HResult == unchecked((int)0x88890004))
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

        private const long FreeSpaceWarnBytes = 500L * 1024 * 1024;
        private const long FreeSpaceBlockBytes = 100L * 1024 * 1024;

        /// <summary>
        /// GAP-09 pre-flight: verifies the output folder exists/is writable (zero-byte probe
        /// file create+delete) and checks free space on its volume. Warns below 500 MB,
        /// blocks below 100 MB. Returns false when recording must not start.
        /// UNC paths (no DriveInfo) skip the space check but still get the probe.
        /// </summary>
        private static bool CheckDiskSpaceAndWritable(string outDir)
        {
            try
            {
                Directory.CreateDirectory(outDir);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(
                    $"Cannot create output folder:\n{outDir}\n{ex.Message}",
                    "Folder Not Writable",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }

            try
            {
                var probe = Path.Combine(outDir, ".hearbud-write-test.tmp");
                File.WriteAllBytes(probe, Array.Empty<byte>());
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(
                    $"Cannot write to output folder:\n{outDir}\n{ex.Message}\n\nCheck permissions or choose another folder.",
                    "Folder Not Writable",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }

            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(outDir));
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady && (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable))
                    {
                        long free = drive.AvailableFreeSpace;
                        double freeMb = free / (1024.0 * 1024.0);
                        if (free < FreeSpaceBlockBytes)
                        {
                            WpfMessageBox.Show(
                                $"Only {freeMb:F0} MB free on {drive.Name}. Recording is blocked below 100 MB to avoid corrupt/truncated files.\n\nFree up space or choose another folder.",
                                "Disk Full",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                            return false;
                        }
                        if (free < FreeSpaceWarnBytes)
                        {
                            var answer = WpfMessageBox.Show(
                                $"Only {freeMb:F0} MB free on {drive.Name} (warning threshold 500 MB).\nA 1-hour stereo session can use ~1.4 GB (mix only) or ~2.8 GB (with stems).\n\nContinue anyway?",
                                "Low Disk Space",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Warning);
                            if (answer != MessageBoxResult.Yes)
                                return false;
                        }
                    }
                }
            }
            catch
            {
                // Non-fatal: UNC/network paths or DriveInfo failures skip the space check (probe already passed).
            }

            return true;
        }

        private async void OnStart(object? sender, RoutedEventArgs? e)
        {
            try
            {
                // GAP-09 #4: a second Start press while counting down aborts it.
                if (_countdownCts != null)
                {
                    _countdownCts.Cancel();
                    return;
                }
                if (_engine.IsRecording) return;

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

                // GAP-09 #4: optional pre-record countdown. Runs BEFORE the
                // disk-guard + engine StartAsync below. Cancellable via the
                // Cancel button or a second Start press (handled at the top).
                if (GetCountdownSeconds() is int countdown && countdown > 0)
                {
                    var cts = new CancellationTokenSource();
                    _countdownCts = cts;
                    CancelCountdownBtn.Visibility = Visibility.Visible;
                    try
                    {
                        for (int i = countdown; i >= 1; i--)
                        {
                            StatusText.Text = $"Starting in {i}…";
                            await Task.Delay(1000, cts.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        StatusText.Text = "Start cancelled.";
                        return;
                    }
                    finally
                    {
                        if (_countdownCts == cts) _countdownCts = null;
                        CancelCountdownBtn.Visibility = Visibility.Collapsed;
                    }
                }

                // GAP-09 free-space / permission guard (UI-only pre-flight; engine disk path untouched).
                // Worst-case write rate: mix float32 stereo @48kHz = 48000*2*4 = 384 kB/s (~23 MB/min),
                // plus 2x int16 stems adds another ~384 kB/s when WriteStems is on (~46 MB/min total,
                // ~2.8 GB for a 1 h session). Thresholds are conservative fixed floors, not estimates:
                // warn below 500 MB (~20 min headroom), block below 100 MB (a few minutes + MP3 temp).
                if (!CheckDiskSpaceAndWritable(outDir))
                    return;

                var micName = MicCombo.SelectedItem as string;
                var spkName = SpeakerCombo.SelectedItem as string;
                bool wantMic = IncludeMicCheck.IsChecked ?? true;
                bool wantSys = RecordSysCheck.IsChecked ?? true;

                if (!wantMic && !wantSys)
                {
                    WpfMessageBox.Show("Keep system audio or microphone on to record.", "Nothing to record",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (wantSys && spkName == null)
                {
                    WpfMessageBox.Show("Select an output device to capture.", "Missing device",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (wantMic && micName == null)
                {
                    WpfMessageBox.Show("Select a microphone.", "Missing device",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _engine.MicGain = MicGain.Value;   // meters & mix balance
                _engine.LoopGain = LoopGain.Value; // meters & mix balance

                await _engine.StartAsync(BuildStartOptions(outDir, baseName));

                // GAP-09: arm optional time/size limits (0 = off; unparsable = off).
                _splitOutDir = outDir;
                _splitBaseName = baseName;
                _splitIndex = 1;
                _limitMinutes = ParseNonNegativeMinutes(LimitMinutesText.Text);
                _limitSizeBytes = ParseNonNegativeSizeBytes(LimitSizeMbText.Text);
                _splitMode = SplitInsteadOfStopCheck.IsChecked ?? false;
                _autoActionRunning = false;
                _suppressStopUi = false;
                _limitTimer.Start();

                StartBtn.IsEnabled = false;
                PauseBtn.IsEnabled = true;
                UpdateHotkeyButtonLabels();
                StopBtn.IsEnabled  = true;
                StatusText.Text = "Recording...";
                _lastAutoReason = null;
                ShowMiniBar();
                UpdateTrayTooltip();
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("OnStart", ex);
            }
        }

        private RecorderStartOptions BuildStartOptions(string outDir, string baseName)
        {
            var micName = MicCombo.SelectedItem as string;
            var spkName = SpeakerCombo.SelectedItem as string;
            bool wantMic = IncludeMicCheck.IsChecked ?? true;
            bool wantSys = RecordSysCheck.IsChecked ?? true;
            return new RecorderStartOptions
            {
                OutputPath = Path.Combine(outDir, baseName),
                LoopbackDeviceId = wantSys && spkName != null ? _spkDict[spkName].DeviceID : null,
                MicDeviceId = wantMic && micName != null ? _micDict[micName].DeviceID : null,
                Mp3BitrateKbps = GetSelectedBitrateKbps(),
                IncludeMic = wantMic,
                WriteStems = WriteStemsCheck.IsChecked ?? true
            };
        }

        private static double ParseNonNegativeMinutes(string? text)
        {
            if (double.TryParse(text, out var v) && !double.IsNaN(v) && v > 0)
                return Math.Min(v, 24 * 60);
            return 0;
        }

        private static long ParseNonNegativeSizeBytes(string? text)
        {
            if (double.TryParse(text, out var v) && !double.IsNaN(v) && v > 0)
                return (long)Math.Min(v * 1024 * 1024, (double)long.MaxValue);
            return 0;
        }

        /// <summary>
        /// GAP-09: 1 s poll of time/size limits while recording. Either limit at 0
        /// means off. Time is wall-clock via engine.RecordElapsed (pause does NOT
        /// stop it — simplest counting, documented in UI comment). Size compares
        /// engine.TotalMixBytesWritten (queued mix payload, WAV header excluded).
        /// Guarded by _autoActionRunning so a slow Stop/Start cannot retrigger.
        /// </summary>
        private async void OnLimitTimerTick(object? sender, EventArgs e)
        {
            _engine.PollCaptureWatchdog();
            if (!_engine.IsRecording || _autoActionRunning) return;
            if (_limitMinutes <= 0 && _limitSizeBytes <= 0) return;
            bool timeHit = _limitMinutes > 0 && _engine.RecordElapsed.TotalMinutes >= _limitMinutes;
            bool sizeHit = _limitSizeBytes > 0 && _engine.TotalMixBytesWritten >= _limitSizeBytes;
            if (!timeHit && !sizeHit) return;

            _autoActionRunning = true;
            try
            {
                string reason = timeHit
                    ? $"time limit ({_limitMinutes:g} min)"
                    : $"size limit ({_limitSizeBytes / (1024.0 * 1024.0):g} MB mix)";
                if (_splitMode)
                    await SplitRecordingAsync(reason);
                else
                {
                    StatusText.Text = $"Auto-stop: {reason} reached — stopping…";
                    _limitTimer.Stop();
                    _lastAutoReason = reason;
                    await _engine.StopAsync();
                }
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("AutoStop", ex);
            }
            finally
            {
                _autoActionRunning = false;
            }
        }

        /// <summary>
        /// GAP-09 split: StopAsync finalizes the current files, then StartAsync
        /// opens a new set with an incremented " (2)", " (3)" suffix. NOT
        /// sample-accurate: there is a short gap covering queue drain + MP3 encode
        /// of the finished part (StopAsync posts the Stopped status, suppressed
        /// here so no "saved" dialog pops mid-session).
        /// </summary>
        private async Task SplitRecordingAsync(string reason)
        {
            _suppressStopUi = true;
            _limitTimer.Stop();
            try
            {
                StatusText.Text = $"Auto-split: {reason} reached — starting new file…";
                await _engine.StopAsync();
                _splitIndex++;
                string newBase = $"{_splitBaseName} ({_splitIndex})";
                BaseNameText.Text = newBase;
                _engine.MicGain = MicGain.Value;
                _engine.LoopGain = LoopGain.Value;
                await _engine.StartAsync(BuildStartOptions(_splitOutDir, newBase));
                StartBtn.IsEnabled = false;
                PauseBtn.IsEnabled = true;
                UpdateHotkeyButtonLabels();
                StopBtn.IsEnabled = true;
                StatusText.Text = "Recording...";
                _limitTimer.Start();
                ShowTrayBalloon("Hearbud: auto-split", $"{reason} — continuing in a new file.");
            }
            finally
            {
                _suppressStopUi = false;
            }
            UpdateTrayTooltip();
        }

        private async void OnStop(object? sender, RoutedEventArgs? e)
        {
            // GAP-09: an auto-stop/split is already driving StopAsync; a concurrent
            // manual Stop could double-encode the MP3. The auto path finishes the
            // stop (or split, after which Stop stays available) on its own.
            if (_autoActionRunning) return;
            StopBtn.IsEnabled = false;
            PauseBtn.IsEnabled = false;
            UpdateHotkeyButtonLabels();
            _limitTimer.Stop();
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

        /// <summary>
        /// GAP-09 #4: reads the Output Settings countdown ComboBox. Only the
        /// listed values (0/3/5/10) are valid; anything else means off.
        /// </summary>
        private int GetCountdownSeconds()
        {
            try
            {
                string? tag = (CountdownCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string
                    ?? CountdownCombo.SelectedValue as string;
                if (int.TryParse(tag, out var v) && v is 0 or 3 or 5 or 10) return v;
            }
            catch { /* best-effort: off */ }
            return 0;
        }

        /// <summary>GAP-09 #4: aborts a running start countdown (button next to StatusText).</summary>
        private void OnCancelCountdown(object sender, RoutedEventArgs e)
        {
            try { _countdownCts?.Cancel(); }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// GAP-09 #4: shows the mini-bar (created on demand, owned by main so
        /// it closes with it). Its buttons delegate to OnPauseResume / OnStop.
        /// </summary>
        private void ShowMiniBar()
        {
            try
            {
                if (_miniBar == null)
                {
                    _miniBar = new MiniBar(
                        () => _engine.RecordElapsed,
                        () => (_micPeak, _sysPeak, _engine.IsPaused && _engine.IsRecording),
                        () => OnPauseResume(null, null),
                        () => OnStop(null, null));
                    _miniBar.Owner = this;
                    _miniBar.Closed += (_, __) => _miniBar = null;
                }
                _miniBar.Show();
                _miniBar.Activate();
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("ShowMiniBar", ex);
            }
        }

        private void OnPauseResume(object? sender, RoutedEventArgs? e)
        {
            try
            {
                if (!_engine.IsRecording) return;
                if (!_engine.IsPaused)
                {
                    _engine.Pause();
                    UpdateHotkeyButtonLabels();
                    StatusText.Text = "Paused — silence while paused";
                }
                else
                {
                    _engine.Resume();
                    UpdateHotkeyButtonLabels();
                    StatusText.Text = "Recording...";
                }
                UpdateTrayTooltip();
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("OnPauseResume", ex);
            }
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

        // GAP-09 #3: sound-test slice. No engine change, nothing persisted.
        private bool _micTestRunning;
        private bool _sysTestRunning;

        /// <summary>
        /// Mic self-test: ensures the live monitor is running (same path as the
        /// auto-monitor on startup) and samples the existing MicBar/MicDb peak
        /// for 3 s. Reports the peak dBFS; the engine is left untouched.
        /// </summary>
        private async void OnTestMic(object sender, RoutedEventArgs e)
        {
            if (_micTestRunning) return;
            try
            {
                if (!(IncludeMicCheck.IsChecked ?? true))
                {
                    WpfMessageBox.Show("Tick \"Include microphone\" so the monitor opens the mic.",
                        "Test Mic", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (MicCombo.SelectedItem as string == null)
                {
                    WpfMessageBox.Show("Select a microphone first.",
                        "Test Mic", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _micTestRunning = true;
                TestMicBtn.IsEnabled = false;
                TryStartAutoMonitor(); // no-op when already monitoring/recording
                await Task.Delay(400);
                StatusText.Text = "Mic test: speak for 3 s…";
                float peak = 0f;
                var until = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < until)
                {
                    if (_micPeak > peak) peak = _micPeak;
                    await Task.Delay(50);
                }
                double db = Dbfs.ToDbfs(peak);
                StatusText.Text = $"Mic test peak: {db:0.0} dBFS";
                WpfMessageBox.Show($"Mic test peak: {db:0.0} dBFS ({peak * 100.0:0}%).\n\nWatch the live Mic meter while speaking; raise Mic Gain if too quiet, lower it on CLIP.",
                    "Test Mic", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("OnTestMic", ex);
            }
            finally
            {
                _micTestRunning = false;
                TestMicBtn.IsEnabled = true;
            }
        }

        /// <summary>
        /// System self-test: synthesizes a 1 kHz / 1 s sine (with 10 ms edge
        /// fades) to a temp WAV and plays it through the selected Speaker
        /// device via CSCore WasapiOut. No assets shipped, no engine change.
        /// </summary>
        private async void OnTestSys(object sender, RoutedEventArgs e)
        {
            if (_sysTestRunning) return;
            try
            {
                var spkName = SpeakerCombo.SelectedItem as string;
                if (string.IsNullOrEmpty(spkName) || !_spkDict.TryGetValue(spkName, out var device) || device == null)
                {
                    WpfMessageBox.Show("Select an output device to capture first.",
                        "Test System", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _sysTestRunning = true;
                TestSysBtn.IsEnabled = false;
                StatusText.Text = "Playing test tone (1 kHz, 1 s)…";
                string tmp = Path.Combine(Path.GetTempPath(), "hearbud-test-tone.wav");
                WriteTestToneWav(tmp, freqHz: 1000.0, seconds: 1.0);
                try
                {
                    // Kept on the UI thread (async waits only): the MMDevice RCW
                    // comes from the UI-thread enumerator, so don't hop threads.
                    using var reader = new CSCore.Codecs.WAV.WaveFileReader(tmp);
                    using var wasapi = new CSCore.SoundOut.WasapiOut() { Device = device };
                    wasapi.Initialize(reader);
                    wasapi.Play();
                    var deadline = DateTime.UtcNow.AddSeconds(6);
                    while (wasapi.PlaybackState == CSCore.SoundOut.PlaybackState.Playing
                           && DateTime.UtcNow < deadline)
                        await Task.Delay(50);
                    try { wasapi.Stop(); } catch { }
                    StatusText.Text = "Test tone played (1 kHz, 1 s).";
                }
                finally
                {
                    try { File.Delete(tmp); } catch { }
                }
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("OnTestSys", ex);
                StatusText.Text = "Test tone failed (see log).";
            }
            finally
            {
                _sysTestRunning = false;
                TestSysBtn.IsEnabled = true;
            }
        }

        /// <summary>Writes a 16-bit stereo PCM WAV sine with short edge fades (click-free).</summary>
        private static void WriteTestToneWav(string path, double freqHz, double seconds, int sampleRate = 44100, short channels = 2)
        {
            int frames = Math.Max(1, (int)(sampleRate * seconds));
            const short bits = 16;
            int blockAlign = channels * bits / 8;
            int dataBytes = frames * blockAlign;
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var w = new BinaryWriter(fs);
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + dataBytes);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);
            w.Write((short)1); // PCM
            w.Write(channels);
            w.Write(sampleRate);
            w.Write(sampleRate * blockAlign);
            w.Write((short)blockAlign);
            w.Write(bits);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            w.Write(dataBytes);
            const double amp = 0.5; // -6 dBFS: clearly audible, headroom before clip
            int fadeFrames = Math.Max(1, (int)(sampleRate * 0.01));
            for (int n = 0; n < frames; n++)
            {
                double env = Math.Min(1.0, Math.Min(n, frames - 1 - n) / (double)fadeFrames);
                double s = amp * env * Math.Sin(2.0 * Math.PI * freqHz * n / sampleRate);
                short v = (short)Math.Clamp((int)Math.Round(s * short.MaxValue), short.MinValue, short.MaxValue);
                for (int c = 0; c < channels; c++) w.Write(v);
            }
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
                    _limitTimer.Stop();
                    EncodingProgressBar.Visibility = Visibility.Collapsed;
                    EncodingProgressText.Visibility = Visibility.Collapsed;

                    // GAP-09 #8: every finished file is history — including
                    // auto-split parts (the suppress flag only skips the
                    // saved-dialog, folder open, and auto-monitor below).
                    RecordHistoryEntry(e);

                    // GAP-09 auto-split handoff: the next StartAsync rearms UI;
                    // skip the saved-dialog, folder open, and auto-monitor here.
                    if (_suppressStopUi) return;

                    // GAP-09 #4: mini-bar only lives while recording.
                    try { _miniBar?.Hide(); } catch { }

                    if (_lastAutoReason != null)
                    {
                        ShowTrayBalloon("Hearbud: auto-stopped", $"{_lastAutoReason} — recording saved.");
                        _lastAutoReason = null;
                    }

                    StartBtn.IsEnabled = true;
                    StopBtn.IsEnabled = false;
                    PauseBtn.IsEnabled = false;
                    UpdateHotkeyButtonLabels();
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

        // ==========================================
        // GAP-09 #8: HISTORY + REPAIR SCAN + PREVIEW
        // Preview is OS-default only (Process.Start in the default player);
        // no embedded MediaElement — a full player is out of scope (§12).
        // ==========================================

        /// <summary>
        /// Appends the finished session to the JSON history (best-effort).
        /// Runs for every Stopped event, including suppressed auto-split parts.
        /// </summary>
        private void RecordHistoryEntry(EngineStatusEventArgs e)
        {
            try
            {
                string mix = e.OutputPathMix ?? "";
                if (string.IsNullOrWhiteSpace(mix)) return;
                string baseName = Path.GetFileNameWithoutExtension(mix);
                if (baseName.EndsWith("-mix", StringComparison.OrdinalIgnoreCase))
                    baseName = baseName[..^4];
                _history.Add(new HistoryEntry
                {
                    Timestamp = DateTime.Now,
                    BaseName = baseName,
                    MixPath = mix,
                    Mp3Path = e.OutputPathMp3 ?? "",
                    SystemPath = e.OutputPathSystem ?? "",
                    MicPath = e.OutputPathMic ?? "",
                    DurationSeconds = _engine.LastRecordElapsed.TotalSeconds,
                    SizeBytes = _engine.TotalMixBytesWritten,
                });
                RefreshHistoryList();
            }
            catch { /* best-effort */ }
        }

        private void RefreshHistoryList()
        {
            try
            {
                HistoryList.ItemsSource = null;
                HistoryList.ItemsSource = _history.Entries;
            }
            catch { /* best-effort */ }
        }

        private HistoryEntry? SelectedHistoryEntry() => HistoryList.SelectedItem as HistoryEntry;

        private static string? FirstExistingFile(params string?[] paths)
        {
            foreach (var p in paths)
            {
                try { if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) return p; }
                catch { /* best-effort */ }
            }
            return null;
        }

        private void OnHistoryDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => RevealSelectedHistoryEntry();

        private void OnHistoryOpen(object sender, RoutedEventArgs e)
            => RevealSelectedHistoryEntry();

        /// <summary>Reveals the selected session file in Explorer (selects it); falls back to the folder.</summary>
        private void RevealSelectedHistoryEntry()
        {
            try
            {
                var entry = SelectedHistoryEntry();
                if (entry == null) { StatusText.Text = "Select a history entry first."; return; }
                string? target = FirstExistingFile(entry.MixPath, entry.Mp3Path, entry.SystemPath, entry.MicPath);
                if (target != null)
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
                    return;
                }
                string? dir = null;
                try
                {
                    string? d = Path.GetDirectoryName(entry.MixPath);
                    if (!string.IsNullOrWhiteSpace(d) && Directory.Exists(d)) dir = d;
                    else if (Directory.Exists(OutputFolderText.Text)) dir = OutputFolderText.Text;
                }
                catch { /* best-effort */ }
                if (dir != null)
                    Process.Start(new ProcessStartInfo() { FileName = dir, UseShellExecute = true, Verb = "open" });
                else
                    StatusText.Text = "Session files no longer exist.";
            }
            catch (Exception ex) { CrashLog.LogAndShow("HistoryReveal", ex); }
        }

        /// <summary>
        /// Minimal preview: opens the session (MP3 preferred, else mix WAV) in the
        /// OS-default player. No embedded MediaElement by design (§12).
        /// </summary>
        private void OnHistoryPlay(object sender, RoutedEventArgs e)
        {
            try
            {
                var entry = SelectedHistoryEntry();
                if (entry == null) { StatusText.Text = "Select a history entry to play."; return; }
                string? target = FirstExistingFile(entry.Mp3Path, entry.MixPath);
                if (target == null)
                {
                    WpfMessageBox.Show("Session file no longer exists on disk.",
                        "Play", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Process.Start(new ProcessStartInfo() { FileName = target, UseShellExecute = true });
            }
            catch (Exception ex) { CrashLog.LogAndShow("HistoryPlay", ex); }
        }

        private void OnHistoryDelete(object sender, RoutedEventArgs e)
        {
            try
            {
                var entry = SelectedHistoryEntry();
                if (entry == null) { StatusText.Text = "Select a history entry to delete."; return; }
                var answer = WpfMessageBox.Show(
                    $"Delete session files for \"{entry.BaseName}\"?\nThis removes the mix, MP3 and stems from disk.",
                    "Delete session",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
                foreach (var f in new[] { entry.MixPath, entry.Mp3Path, entry.SystemPath, entry.MicPath })
                {
                    try { if (!string.IsNullOrWhiteSpace(f) && File.Exists(f)) File.Delete(f); }
                    catch { /* best-effort */ }
                }
                _history.Remove(entry);
                RefreshHistoryList();
            }
            catch (Exception ex) { CrashLog.LogAndShow("HistoryDelete", ex); }
        }

        /// <summary>
        /// Startup scan of the output folder for unfinalized WAVs (crash/abort
        /// leftovers). Fixable headers are repaired in place; the rest are
        /// listed with a delete offer. Best-effort.
        /// </summary>
        private void RunStartupRepairScan()
        {
            try
            {
                var suspects = WavRepair.ScanDirectory(OutputFolderText.Text);
                if (suspects.Count == 0) return;
                int repaired = 0;
                var unrepairable = new List<string>();
                foreach (var f in suspects)
                {
                    try
                    {
                        if (WavRepair.TryRepair(f)) repaired++;
                        else unrepairable.Add(f);
                    }
                    catch { unrepairable.Add(f); }
                }
                if (unrepairable.Count > 0)
                {
                    int show = Math.Min(10, unrepairable.Count);
                    string list = string.Join("\n", unrepairable.Take(show).Select(Path.GetFileName));
                    if (unrepairable.Count > show) list += $"\n… and {unrepairable.Count - show} more.";
                    var answer = WpfMessageBox.Show(
                        $"{repaired} file(s) repaired.\n\n{unrepairable.Count} unrepairable file(s) (empty or corrupt header):\n{list}\n\nDelete them?",
                        "Unfinished recordings found",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer == MessageBoxResult.Yes)
                        foreach (var f in unrepairable)
                            try { if (File.Exists(f)) File.Delete(f); } catch { }
                }
                else if (repaired > 0)
                {
                    StatusText.Text = $"Repaired {repaired} unfinalized recording(s).";
                }
            }
            catch { /* best-effort */ }
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
                // GAP-09 #4: only hearbud.ico ships, so there is no icon swap per
                // state — state is conveyed via tooltip text + these Pause/Stop
                // items (+ balloon tips on auto-stop/split) instead.
                var contextMenu = new System.Windows.Controls.ContextMenu();

                _trayPauseItem = new System.Windows.Controls.MenuItem { Header = "Pause", IsEnabled = false };
                _trayPauseItem.Click += OnPauseResume;
                contextMenu.Items.Add(_trayPauseItem);

                _trayStopItem = new System.Windows.Controls.MenuItem { Header = "Stop", IsEnabled = false };
                _trayStopItem.Click += OnStop;
                contextMenu.Items.Add(_trayStopItem);

                contextMenu.Items.Add(new System.Windows.Controls.Separator());

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

            // GAP-09 #4: abort any pending start countdown; the mini-bar is
            // Owner-bound so it closes with main, but close explicitly too.
            try { _countdownCts?.Cancel(); } catch { }
            try { _miniBar?.Close(); } catch { }

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
            Dispose();

            try
            {
                // Persist current UI selections
                _settings.MicName = MicCombo.SelectedItem as string ?? _settings.MicName;
                _settings.SpeakerName = SpeakerCombo.SelectedItem as string ?? _settings.SpeakerName;
                _settings.OutputDir = OutputFolderText.Text;
                _settings.MicGain = MicGain.Value;
                _settings.LoopGain = LoopGain.Value;
                _settings.Mp3BitrateKbps = GetSelectedBitrateKbps();
                _settings.WriteStems = WriteStemsCheck.IsChecked ?? true;
                _settings.IncludeMic = IncludeMicCheck.IsChecked ?? true;
                _settings.IncludeSystem = RecordSysCheck.IsChecked ?? true;
                _settings.AutoStopMinutes = ParseNonNegativeMinutes(LimitMinutesText.Text);
                _settings.AutoStopSizeMb = ParseNonNegativeSizeBytes(LimitSizeMbText.Text) / (1024.0 * 1024.0);
                _settings.SplitInsteadOfStop = SplitInsteadOfStopCheck.IsChecked ?? false;
                _settings.CountdownSeconds = GetCountdownSeconds();
                _settings.Save();
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// CA1001: disposes owned <see cref="_engine"/>, <see cref="_notifEnumerator"/>,
        /// <see cref="_trayIcon"/> and <see cref="_uiTimer"/>. Called from
        /// <see cref="OnClosed(EventArgs)"/>; idempotent so double-close is safe.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _uiTimer.Stop(); } catch { }
            try { _trayIcon?.Dispose(); } catch { }

            // Unregister device notifications before disposing the enumerator.
            try { _notifEnumerator?.UnregisterEndpointNotificationCallback(this); } catch { }
            try { _notifEnumerator?.Dispose(); } catch { }
            _notifEnumerator = null;

            _engine.Dispose();
            GC.SuppressFinalize(this);
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

                // GAP-09 #9 mapping slice: single persisted value
                // (AppSettings.Mp3BitrateKbps) drives two controls.
                // 0 -> WAV master (bitrate box disabled, parked at 192);
                // >0 -> WAV + MP3 @ that bitrate (unknown values fall back to 192).
                var persisted = _settings.Mp3BitrateKbps < 0 ? 192 : _settings.Mp3BitrateKbps;
                bool wavOnly = persisted == 0;
                OutputFormatCombo.SelectedValue = wavOnly ? "wav" : "mp3";
                if (OutputFormatCombo.SelectedItem as ComboBoxItem == null)
                    OutputFormatCombo.SelectedValue = "mp3";
                OutputQualityCombo.SelectedValue = wavOnly ? "192" : persisted.ToString(CultureInfo.InvariantCulture);
                if (OutputQualityCombo.SelectedItem as ComboBoxItem == null)
                    OutputQualityCombo.SelectedValue = "192";
                UpdateBitrateEnabled();

                WriteStemsCheck.IsChecked = _settings.WriteStems;
                IncludeMicCheck.IsChecked = _settings.IncludeMic;
                RecordSysCheck.IsChecked = _settings.IncludeSystem;
                LimitMinutesText.Text = _settings.AutoStopMinutes > 0 ? _settings.AutoStopMinutes.ToString(CultureInfo.InvariantCulture) : "";
                LimitSizeMbText.Text = _settings.AutoStopSizeMb > 0 ? _settings.AutoStopSizeMb.ToString(CultureInfo.InvariantCulture) : "";
                SplitInsteadOfStopCheck.IsChecked = _settings.SplitInsteadOfStop;
                CountdownCombo.SelectedValue = _settings.CountdownSeconds switch { 3 => "3", 5 => "5", 10 => "10", _ => "0" };

                // GAP-09 #6: show persisted hotkeys, then rebuild gestures.
                if (StartHotkeyBox != null) StartHotkeyBox.Text = _settings.StartHotkey;
                if (PauseHotkeyBox != null) PauseHotkeyBox.Text = _settings.PauseHotkey;
                if (StopHotkeyBox != null) StopHotkeyBox.Text = _settings.StopHotkey;
                RebuildInputBindings();
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("LoadSettingsToUi", ex);
            }
        }

        /// <summary>
        /// GAP-09 #9 mapping slice (UI-only): two controls, one persisted value.
        /// Format "wav" (WAV master) forces 0 regardless of the bitrate box;
        /// format "mp3" (WAV + MP3) uses the bitrate box, clamped to 64..320.
        /// Unknown/unset format defaults to MP3-included to preserve the
        /// historical default (WAV + MP3 @ 192).
        /// </summary>
        private bool IsMp3Included()
        {
            try
            {
                string? tag = (OutputFormatCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string
                    ?? OutputFormatCombo.SelectedValue as string;
                if (string.Equals(tag, "wav", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(tag, "mp3", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { /* best-effort: default below */ }
            return true;
        }

        private void UpdateBitrateEnabled()
        {
            try
            {
                if (OutputQualityCombo != null)
                    OutputQualityCombo.IsEnabled = IsMp3Included();
            }
            catch { /* best-effort */ }
        }

        private void OnOutputFormatChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateBitrateEnabled();
        }

        private int GetSelectedBitrateKbps()
        {
            try
            {
                // WAV master ignores the bitrate box entirely.
                if (!IsMp3Included()) return 0;
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

        // ==========================================
        // GAP-09 #6: REMAPPABLE IN-APP HOTKEYS
        // Window-focused scope only (plain InputBindings; no RegisterHotKey /
        // HwndSource global hooks — system-wide capture is phase 2).
        // ==========================================

        /// <summary>
        /// Clears and re-adds the Start/Pause/Stop KeyBindings from the current
        /// settings, then refreshes button labels. Invalid gestures are skipped
        /// (settings are validated on load/capture so this should not happen);
        /// duplicates are skipped (capture-time conflict check prevents them).
        /// </summary>
        private void RebuildInputBindings()
        {
            try
            {
                InputBindings.Clear();
                var map = new (string Text, ICommand Command)[]
                {
                    (_settings.StartHotkey, ApplicationCommands.New),
                    (_settings.StopHotkey, ApplicationCommands.Stop),
                    (_settings.PauseHotkey, MediaCommands.Pause),
                };
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var conv = new KeyGestureConverter();
                foreach (var (text, command) in map)
                {
                    if (!AppSettings.TryNormalizeHotkey(text, out var normalized))
                        continue;
                    if (!seen.Add(normalized))
                        continue;
                    try
                    {
                        if (conv.ConvertFromString(normalized) is KeyGesture gesture)
                            InputBindings.Add(new KeyBinding(command, gesture));
                    }
                    catch { /* skip one bad gesture, keep the rest */ }
                }
                UpdateHotkeyButtonLabels();
            }
            catch { /* best-effort: window works without shortcuts */ }
        }

        /// <summary>Refreshes Start/Pause/Stop button labels + AcceleratorKey from settings.</summary>
        private void UpdateHotkeyButtonLabels()
        {
            try
            {
                if (StartBtn != null)
                {
                    StartBtn.Content = $"Start Recording ({_settings.StartHotkey})";
                    System.Windows.Automation.AutomationProperties.SetAcceleratorKey(StartBtn, _settings.StartHotkey);
                }
                if (PauseBtn != null)
                {
                    bool paused = _engine.IsPaused && _engine.IsRecording;
                    PauseBtn.Content = paused ? $"Resume ({_settings.PauseHotkey})" : $"Pause ({_settings.PauseHotkey})";
                    System.Windows.Automation.AutomationProperties.SetAcceleratorKey(PauseBtn, _settings.PauseHotkey);
                }
                if (StopBtn != null)
                {
                    StopBtn.Content = $"Stop ({_settings.StopHotkey})";
                    System.Windows.Automation.AutomationProperties.SetAcceleratorKey(StopBtn, _settings.StopHotkey);
                }
            }
            catch { /* best-effort */ }
        }

        private void OnHotkeyBoxGotFocus(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is System.Windows.Controls.TextBox tb)
                {
                    tb.SelectAll();
                    StatusText.Text = "Press the new shortcut (Esc cancels)…";
                }
            }
            catch { /* best-effort */ }
        }

        private void OnHotkeyBoxLostFocus(object sender, RoutedEventArgs e)
        {
            try
            {
                // Restore canonical text in case focus left mid-capture.
                if (sender is System.Windows.Controls.TextBox tb && tb.Tag is string tag)
                    tb.Text = HotkeyFor(tag);
            }
            catch { /* best-effort */ }
        }

        private string HotkeyFor(string tag) => tag switch
        {
            "Start" => _settings.StartHotkey,
            "Pause" => _settings.PauseHotkey,
            "Stop" => _settings.StopHotkey,
            _ => "",
        };

        /// <summary>
        /// Click-to-capture: converts the pressed key + current modifiers into a
        /// canonical gesture string. Bare-modifier presses are ignored; a
        /// modifier-less key or Escape cancels. Duplicates across the three
        /// actions warn and keep the old value; otherwise the new gesture is
        /// persisted and bindings are rebuilt.
        /// </summary>
        private void OnHotkeyCapturePreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (sender is not System.Windows.Controls.TextBox tb || tb.Tag is not string tag)
                return;
            try
            {
                e.Handled = true;

                // Resolve the real key first: with Alt held, e.Key is
                // Key.System and the physical key is in e.SystemKey.
                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.ImeProcessed)
                    key = e.ImeProcessedKey;

                // Ignore bare modifier presses (user is still chording).
                if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                    or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
                    return;

                // Escape with no modifiers cancels the capture.
                if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
                {
                    tb.Text = HotkeyFor(tag);
                    StatusText.Text = "Shortcut capture cancelled.";
                    MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    return;
                }

                var mods = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows);
                if (mods == ModifierKeys.None)
                {
                    StatusText.Text = "Shortcut needs a modifier (Ctrl/Alt/Shift) — keeping old.";
                    return;
                }

                string? gestureText;
                try
                {
                    gestureText = new KeyGestureConverter().ConvertToString(new KeyGesture(key, mods));
                }
                catch
                {
                    StatusText.Text = "That key can't be used as a shortcut — keeping old.";
                    return;
                }
                if (string.IsNullOrWhiteSpace(gestureText) ||
                    !AppSettings.TryNormalizeHotkey(gestureText, out var normalized))
                {
                    StatusText.Text = "That key can't be used as a shortcut — keeping old.";
                    return;
                }

                // Conflict check: duplicate gesture → warning, keep old.
                string other1, other2;
                if (tag == "Start") { other1 = _settings.PauseHotkey; other2 = _settings.StopHotkey; }
                else if (tag == "Pause") { other1 = _settings.StartHotkey; other2 = _settings.StopHotkey; }
                else if (tag == "Stop") { other1 = _settings.StartHotkey; other2 = _settings.PauseHotkey; }
                else return;
                if (string.Equals(normalized, other1, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalized, other2, StringComparison.OrdinalIgnoreCase))
                {
                    WpfMessageBox.Show(
                        $"\"{normalized}\" is already assigned to another action.\nKeeping the old shortcut.",
                        "Shortcut already in use",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    tb.Text = HotkeyFor(tag);
                    return;
                }

                if (tag == "Start") _settings.StartHotkey = normalized;
                else if (tag == "Pause") _settings.PauseHotkey = normalized;
                else _settings.StopHotkey = normalized;
                _settings.Save();
                tb.Text = normalized;
                RebuildInputBindings();
                StatusText.Text = $"Shortcut set: {tag} = {normalized}";
                MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("OnHotkeyCapture", ex);
            }
        }

        private void OnResetHotkeys(object sender, RoutedEventArgs e)
        {
            try
            {
                _settings.StartHotkey = AppSettings.DefaultStartHotkey;
                _settings.PauseHotkey = AppSettings.DefaultPauseHotkey;
                _settings.StopHotkey = AppSettings.DefaultStopHotkey;
                _settings.Save();
                if (StartHotkeyBox != null) StartHotkeyBox.Text = _settings.StartHotkey;
                if (PauseHotkeyBox != null) PauseHotkeyBox.Text = _settings.PauseHotkey;
                if (StopHotkeyBox != null) StopHotkeyBox.Text = _settings.StopHotkey;
                RebuildInputBindings();
                StatusText.Text = "Shortcuts reset to Ctrl+R / Ctrl+P / Ctrl+S.";
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("OnResetHotkeys", ex);
            }
        }

        // ==========================================
        // DEVICE NOTIFICATION (IMMNotificationClient)
        // ==========================================

        /// <summary>
        /// Fires when a new audio endpoint device is added to the system (e.g. USB headset plugged in).
        /// Refreshes device combos and restarts auto-monitoring so meters stay live.
        /// </summary>
        public void OnDeviceAdded(string deviceId)
        {
            // Callbacks arrive on a COM background thread — marshal everything back to the UI thread.
            Dispatcher.BeginInvoke(async () =>
            {
                SafeRefreshDevices();
                UpdateTrayTooltip();
                TryStartAutoMonitor();
                await _engine.TryResumeLostSourcesAsync();
            });
        }

        /// <summary>
        /// Fires when an audio endpoint device is removed (e.g. USB headset unplugged).
        /// If the removed device is the one currently being used for recording or monitoring,
        /// the engine will stop safely before the combos are refreshed.
        /// </summary>
        public void OnDeviceRemoved(string deviceId)
        {
            Dispatcher.BeginInvoke(async () =>
            {
                // GAP-06: engine keeps the file open and bridges silence; only refresh combos here.
                await _engine.HandleDeviceDisconnectedAsync(deviceId);
                SafeRefreshDevices();
                UpdateTrayTooltip();
            });
        }

        /// <summary>
        /// Fires when an existing device changes state (e.g. headphone jack goes from Active to UnPlugged).
        /// Routes to OnDeviceAdded/Removed so the same logic applies.
        /// </summary>
        public void OnDeviceStateChanged(string deviceId, DeviceState deviceState)
        {
            if (deviceState == DeviceState.Active)
                OnDeviceAdded(deviceId);
            else if (deviceState is DeviceState.UnPlugged or DeviceState.NotPresent)
                OnDeviceRemoved(deviceId);
        }

        /// <summary>Not used — we don't need to react to default-device changes.</summary>
        public void OnDefaultDeviceChanged(DataFlow dataFlow, Role role, string deviceId) { }

        /// <summary>Not used — we don't react to property-value changes on devices.</summary>
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }

        /// <summary>
        /// Updates the system tray tooltip to reflect whether recording is active
        /// and which output device is selected. Also refreshes the Pause/Stop
        /// menu items (GAP-09 #4: no icon assets to swap, so text carries state).
        /// </summary>
        private void UpdateTrayTooltip()
        {
            if (_trayIcon == null) return;
            var spkName = SpeakerCombo.SelectedItem as string;
            var micName = MicCombo.SelectedItem as string;

            _trayIcon.ToolTipText = _engine.IsRecording
                ? (_engine.IsPaused
                    ? $"Paused \u2014 {spkName ?? "No device"}"
                    : $"\u25cf Recording \u2014 {spkName ?? "No device"}")
                : spkName != null
                    ? $"Hearbud \u2014 {spkName}"
                    : "Hearbud \u2014 No device connected";

            if (_trayPauseItem != null)
            {
                _trayPauseItem.Header = _engine.IsPaused ? "Resume" : "Pause";
                _trayPauseItem.IsEnabled = _engine.IsRecording;
            }
            if (_trayStopItem != null)
                _trayStopItem.IsEnabled = _engine.IsRecording;
        }

        /// <summary>GAP-09 #4: tray balloon tip (auto-stop / auto-split). Best-effort.</summary>
        private void ShowTrayBalloon(string title, string message)
        {
            try { _trayIcon?.ShowNotification(title, message, H.NotifyIcon.Core.NotificationIcon.Info); }
            catch { /* best-effort */ }
        }
    }
}
