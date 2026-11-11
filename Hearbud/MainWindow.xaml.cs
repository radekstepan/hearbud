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
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using WpfMessageBox = System.Windows.MessageBox;
using WinFormsFolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using H.NotifyIcon;

namespace Hearbud
{
    public partial class MainWindow : Window, IMMNotificationClient, IDisposable
    {
    private readonly RecorderEngine _engine = new();
    // UI meter pump: DispatcherTimer (UI thread, coalesced) instead of
    // System.Timers.Timer + blocking Dispatcher.Invoke, which queued up
    // behind slow device-enum/audio-start and starved input (checkbox/
    // dropdown lag immediately on launch).
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private System.Windows.Media.Brush? _clipBrush;
    private System.Windows.Media.Brush? _disabledBrush;
    private int _lastEncodePercent = -1;
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

    // GAP-09 #4: pre-record countdown + tray states.
    private CancellationTokenSource? _countdownCts;
    private System.Windows.Controls.MenuItem? _trayPauseItem;
    private System.Windows.Controls.MenuItem? _trayStopItem;
    private string? _lastAutoReason;

    private AppSettings _settings = AppSettings.Load();
        private readonly Dictionary<string, MMDevice> _micDict = new();
        private readonly Dictionary<string, MMDevice> _spkDict = new();

        private bool _uiReady;

    private bool _disposed;

    // Click-glitch diagnostics: env dump once + 1 Hz lag/input summary, tail-capped.
    private readonly InputEventRing _diagInput = new(50);
    private readonly LagTracker _diagLag = new();
    private DispatcherTimer? _diagTimer;
    private long _diagLastTick;
    private int _diagTickCount;
    private int _diagMouseDown;
    private int _diagMouseUp;
    private int _diagStylusDown;
    private int _diagLastMouseDown;
    private int _diagLastMouseUp;

    // Non-client/close diagnostics: the title-bar X is a Win32 message, not a
    // WPF mouse event, so the input ring above cannot see it. These log the
    // real click -> WM_CLOSE -> OnClosing timeline. Frame counters prove
    // whether the WPF render thread is presenting while the UI thread ticks.
    private long _ctorTick;
    private HwndSource? _hwndSource;
    private long _frameCount;
    private long _firstFrameTick;
    private long _lastFrameTick;
    private long _maxFrameGapTicks;

        public MainWindow()
        {
            try
            {
                _ctorTick = Stopwatch.GetTimestamp();
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

                    // Resolve meter brushes once (UpdateMeters runs at 10 Hz;
                    // per-tick FindResource walks the tree and adds jank).
                    try { _clipBrush = (System.Windows.Media.Brush)FindResource("ClipBrush"); } catch { }
                    try { _disabledBrush = (System.Windows.Media.Brush)FindResource("ControlDisabled"); } catch { }

                    // Display version
                    var version = Assembly.GetExecutingAssembly().GetName().Version;
                    if (version != null)
                    {
                        VersionText.Text = $"v{version.Major}.{version.Minor}.{version.Build}";
                    }

                    // Auto-start live monitoring so meters show immediately,
                    // but only after the first frame: WASAPI init on the UI
                    // thread before the window is presented could delay the
                    // first paint on slow USB audio drivers.
                    Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                        new Action(() => TryStartAutoMonitor()));

                    // Register for Windows audio device notifications (plug/unplug).
                    // Must be done after the window handle exists (i.e. inside Loaded).
                    _notifEnumerator = new MMDeviceEnumerator();
                    _notifEnumerator.RegisterEndpointNotificationCallback(this);

                    // Click-glitch diagnostics: env snapshot once per start.
                    try { CrashLog.LogOnly("UiDiagnostics.Env", UiDiagnostics.BuildEnvReport(this)); } catch { }
                    _diagLastTick = Stopwatch.GetTimestamp();
                    try { _diagTimer?.Start(); } catch { }

                    // USB layout check (best-effort, off the UI thread): a mic
                    // and pointing device behind one hub can drop mouse input
                    // entirely before Windows delivers it, which looks exactly
                    // like a frozen app. Never block startup.
                    CheckUsbLayout();

                    // A window launched by a script/agent can open behind the
                    // foreground window; the first title-bar clicks then never
                    // reach it. Make the launched window active explicitly.
                    try { Activate(); } catch { }
                };

                _engine.LevelChanged += OnEngineLevelChanged;
                _engine.Status += OnEngineStatus;
                _engine.EncodingProgress += OnEngineEncodingProgress;

                _uiTimer.Tick += (_, __) => UpdateMeters();
                _uiTimer.Start();

                _limitTimer.Tick += OnLimitTimerTick;

                SetupUiDiagnostics();

                // Render-thread probe: ContentRendered proves the first frame
                // reached the screen; Rendering counts presented frames. A GUI
                // that looks frozen while the UI thread timer still ticks shows
                // up here as frames=0 or a large maxGap.
                ContentRendered += (_, __) =>
                {
                    try
                    {
                        CrashLog.LogOnly("UiRender",
                            $"t={DateTime.Now:HH:mm:ss.fff} ContentRendered after {Ms(_ctorTick)}ms " +
                            $"size={ActualWidth:F0}x{ActualHeight:F0} left={Left:F0} top={Top:F0} " +
                            $"state={WindowState} active={IsActive} tid={Environment.CurrentManagedThreadId}");
                    }
                    catch { /* best-effort */ }
                };
                try { CompositionTarget.Rendering += OnCompositionFrame; } catch { }
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("MainWindow.ctor", ex);
                throw;
            }
        }

        private void OnToggleTheme(object sender, RoutedEventArgs e) { /* hidden; no-op */ }

        /// <summary>
        /// Runs the USB topology probe off the UI thread for the selected mic.
        /// A mic and pointing device behind one hub can drop mouse input before
        /// Windows delivers it, which looks exactly like the app freezing; the
        /// inline banner makes the risk visible, and the probe never blocks.
        /// Called at startup and from Refresh Devices (so rewiring can be
        /// re-checked without restarting).
        /// </summary>
        private void CheckUsbLayout()
        {
            try
            {
                try { UsbWarningBanner.Visibility = Visibility.Collapsed; } catch { }
                if (MicCombo.SelectedItem is not string micName ||
                    !_micDict.TryGetValue(micName, out MMDevice? micDevice))
                    return;

                string micId = micDevice.DeviceID;
                string micLabel = micDevice.FriendlyName ?? micName;
                _ = Task.Run(() =>
                {
                    string? found = UsbTopologyProbe.Check(micId, micLabel);
                    if (found == null)
                    {
                        CrashLog.LogOnly("UiDiagnostics.Usb",
                            "OK: selected mic and pointing devices do not share a non-root USB hub.");
                        return;
                    }
                    string warning = found;
                    CrashLog.LogOnly("UiDiagnostics.Usb", warning);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            UsbWarningText.Text = warning;
                            UsbWarningBanner.Visibility = Visibility.Visible;
                            MicCombo.ToolTip = warning;
                            ShowTrayBalloon("Hearbud: USB layout warning", warning);
                        }
                        catch { /* best-effort */ }
                    }));
                });
            }
            catch { /* diagnostics must never affect startup */ }
        }

        private void OnRefreshDevices(object sender, RoutedEventArgs e)
        {
            SafeRefreshDevices();
            TryStartAutoMonitor(); // keep meters alive after device changes
            CheckUsbLayout();      // re-check after the user rewires USB
        }

        /// <summary>
        /// Include toggles sit next to the device names. Unchecking mutes that
        /// source in software: its combo is disabled synchronously so the
        /// click paints instantly, then a pool thread (never the UI thread)
        /// applies the mute via <see cref="RecorderEngine.MonitorSourcesAsync"/>,
        /// which never stops or restarts a running capture (the device is only
        /// opened when a wanted side is missing). Skipped while recording
        /// (sources are fixed for the running file). Every step is timestamped
        /// to the debugger ("AudioRestart") so a laggy click can be traced.
        /// </summary>
        private readonly object _restartLock = new();
        private Task _restartChain = Task.CompletedTask;

        private void OnIncludeSourceChanged(object sender, RoutedEventArgs e)
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                // Checked/Unchecked can fire during InitializeComponent before
                // every x:Name field is connected; a null here used to throw.
                if (IncludeMicCheck == null || RecordSysCheck == null ||
                    MicCombo == null || SpeakerCombo == null) return;
                bool wantMic = IncludeMicCheck.IsChecked ?? true;
                bool wantSys = RecordSysCheck.IsChecked ?? true;
                MicCombo.IsEnabled = wantMic;
                SpeakerCombo.IsEnabled = wantSys;
                CrashLog.LogOnly("AudioRestart",
                    $"t={DateTime.Now:HH:mm:ss.fff} click mic={wantMic} sys={wantSys} uiReady={_uiReady} recording={_engine.IsRecording} tid={Environment.CurrentManagedThreadId}");
                if (!_uiReady || _engine.IsRecording) return;

                var micName = MicCombo.SelectedItem as string;
                var spkName = SpeakerCombo.SelectedItem as string;
                if (wantMic && (micName == null || !_micDict.ContainsKey(micName)))
                {
                    CrashLog.LogOnly("AudioRestart", $"t={DateTime.Now:HH:mm:ss.fff} no mic selected, restart skipped");
                    return;
                }
                if (wantSys && (spkName == null || !_spkDict.ContainsKey(spkName)))
                {
                    CrashLog.LogOnly("AudioRestart", $"t={DateTime.Now:HH:mm:ss.fff} no output selected, restart skipped");
                    return;
                }
                var opts = new RecorderStartOptions
                {
                    LoopbackDeviceId = wantSys ? _spkDict[spkName!].DeviceID : null,
                    MicDeviceId = wantMic ? _micDict[micName!].DeviceID : null,
                };
                lock (_restartLock)
                {
                    _restartChain = _restartChain.ContinueWith(async _ =>
                    {
                        long s0 = Stopwatch.GetTimestamp();
                        try
                        {
                            await _engine.MonitorSourcesAsync(opts).ConfigureAwait(false);
                            CrashLog.LogOnly("AudioRestart",
                                $"t={DateTime.Now:HH:mm:ss.fff} toggle done +{(Stopwatch.GetTimestamp() - s0) * 1000.0 / Stopwatch.Frequency:F0}ms (pool thread, UI never blocked)");
                        }
                        catch (Exception ex)
                        {
                            CrashLog.LogOnly("AudioRestart",
                                $"t={DateTime.Now:HH:mm:ss.fff} toggle FAILED after {(Stopwatch.GetTimestamp() - s0) * 1000.0 / Stopwatch.Frequency:F0}ms: {ex.GetType().Name}: {ex.Message}");
                        }
                    }, TaskScheduler.Default).Unwrap();
                }
                CrashLog.LogOnly("AudioRestart",
                    $"t={DateTime.Now:HH:mm:ss.fff} click handler returned in {(Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency:F1}ms (UI thread free)");
            }
            catch (Exception ex)
            {
                CrashLog.LogOnly("AudioRestart",
                    $"t={DateTime.Now:HH:mm:ss.fff} click handler EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Locks a source off while its side has no devices: the checkbox is
        /// unchecked and disabled so it cannot be ticked, and the dropdown is
        /// disabled with a cleared selection. Once a refresh finds 1+ devices
        /// the checkbox is unlocked (checked state left as-is) and the
        /// dropdown follows it again. While recording, sources are fixed for
        /// the running file, so everything stays frozen but checked state is
        /// never touched.
        /// </summary>
        private void ApplySideAvailability(bool hasDevices,
            System.Windows.Controls.CheckBox box,
            System.Windows.Controls.ComboBox combo)
        {
            if (_engine.IsRecording)
            {
                box.IsEnabled = false;
                combo.IsEnabled = false;
                return;
            }
            if (!hasDevices)
            {
                combo.SelectedItem = null;
                box.IsChecked = false;
                box.IsEnabled = false;
                combo.IsEnabled = false;
            }
            else
            {
                box.IsEnabled = true;
                combo.IsEnabled = box.IsChecked ?? true;
            }
        }

        private void SafeRefreshDevices()
        {
            try
            {
                // Dispose prior COM devices before dropping them (leaked
                // MMDevice RCWs accumulated handles on every refresh/notify).
                foreach (var d in _micDict.Values)
                    try { d.Dispose(); } catch { }
                foreach (var d in _spkDict.Values)
                    try { d.Dispose(); } catch { }
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
                    // OBS_LESSONS 3.5: OBS defaults speakers to eConsole (not
                    // Multimedia). Match it so "Default Device" agrees with OBS.
                    // Console first (OBS win-wasapi.cpp:613-615 Desktop Audio
                    // uses eRender/eConsole), Multimedia fallback when
                    // Console != Multimedia.
                    string? defName = null;
                    try
                    {
                        var defConsole = e3.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                        if (defConsole != null && _spkDict.ContainsKey(defConsole.FriendlyName))
                            defName = defConsole.FriendlyName;
                    }
                    catch { }
                    if (defName == null)
                    {
                        try
                        {
                            var defMulti = e3.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                            if (defMulti != null && _spkDict.ContainsKey(defMulti.FriendlyName))
                                defName = defMulti.FriendlyName;
                        }
                        catch { }
                    }
                    if (defName != null)
                        SpeakerCombo.SelectedItem = defName;
                }
                catch { }

                if (MicCombo.SelectedItem == null && _micDict.Count > 0)
                    MicCombo.SelectedItem = _micDict.Keys.First();
                if (SpeakerCombo.SelectedItem == null && _spkDict.Count > 0)
                    SpeakerCombo.SelectedItem = _spkDict.Keys.First();
                ApplySideAvailability(_micDict.Count > 0, IncludeMicCheck, MicCombo);
                ApplySideAvailability(_spkDict.Count > 0, RecordSysCheck, SpeakerCombo);

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
            long t0 = Stopwatch.GetTimestamp();
            CrashLog.LogOnly("AudioEngine",
                $"t={DateTime.Now:HH:mm:ss.fff} TryStartAutoMonitor entry tid={Environment.CurrentManagedThreadId}");
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
                CrashLog.LogOnly("AudioEngine",
                    $"t={DateTime.Now:HH:mm:ss.fff} TryStartAutoMonitor done +{(Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency:F0}ms");
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

                // GAP-09: arm optional time/size limits from Settings (0 = off).
                _splitOutDir = outDir;
                _splitBaseName = baseName;
                _splitIndex = 1;
                _limitMinutes = _settings.AutoStopMinutes;
                _limitSizeBytes = (long)Math.Min(_settings.AutoStopSizeMb * 1024 * 1024, (double)long.MaxValue);
                _splitMode = _settings.SplitInsteadOfStop;
                _autoActionRunning = false;
                _suppressStopUi = false;
                _limitTimer.Start();

                StartBtn.IsEnabled = false;
                PauseBtn.IsEnabled = true;
                UpdateHotkeyButtonLabels();
                StopBtn.IsEnabled  = true;
                // Sources are fixed for the running file: freeze the
                // include checkboxes and device dropdowns.
                ApplySideAvailability(_micDict.Count > 0, IncludeMicCheck, MicCombo);
                ApplySideAvailability(_spkDict.Count > 0, RecordSysCheck, SpeakerCombo);
                StatusText.Text = "Recording...";
                _lastAutoReason = null;
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
                WriteStems = _settings.WriteStems
            };
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
                // Still recording into the next part: keep sources frozen.
                ApplySideAvailability(_micDict.Count > 0, IncludeMicCheck, MicCombo);
                ApplySideAvailability(_spkDict.Count > 0, RecordSysCheck, SpeakerCombo);
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
        /// GAP-09 #4: pre-record countdown from Settings. Only the listed
        /// values (0/3/5/10) are valid; anything else means off.
        /// </summary>
        private int GetCountdownSeconds()
        {
            int v = _settings.CountdownSeconds;
            return v is 0 or 3 or 5 or 10 ? v : 0;
        }

        /// <summary>GAP-09 #4: aborts a running start countdown (button next to StatusText).</summary>
        private void OnCancelCountdown(object sender, RoutedEventArgs e)
        {
            try { _countdownCts?.Cancel(); }
            catch { /* best-effort */ }
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
            MicGainLabel.Text  = Dbfs.FormatGain(MicGain.Value)
                + (MicGain.Value > 2.0 ? " — high, move mic closer" : "");
            LoopGainLabel.Text = Dbfs.FormatGain(LoopGain.Value);
        }

        private volatile float _micPeak;
        private volatile float _sysPeak;
        private volatile float _mixPeak;
        private DateTime _micClipUntil = DateTime.MinValue;
        private DateTime _sysClipUntil = DateTime.MinValue;
        private DateTime _mixClipUntil = DateTime.MinValue;

        private void OnEngineLevelChanged(object? sender, LevelChangedEventArgs e)
        {
            if (e.Source == LevelSource.Mic) _micPeak = e.Peak;
            else if (e.Source == LevelSource.System) _sysPeak = e.Peak;
            else _mixPeak = e.Peak;

            if (e.Clipped)
            {
                if (e.Source == LevelSource.Mic) _micClipUntil = DateTime.Now.AddMilliseconds(1500);
                else if (e.Source == LevelSource.System) _sysClipUntil = DateTime.Now.AddMilliseconds(1500);
                else _mixClipUntil = DateTime.Now.AddMilliseconds(1500);
            }
        }

        private void UpdateMeters()
        {
            try
            {
                var mp = Math.Clamp(_micPeak, 0f, 1f);
                var sp = Math.Clamp(_sysPeak, 0f, 1f);
                var xp = Math.Clamp(_mixPeak, 0f, 1f);

                MicBar.Value = Math.Round(mp * 100, 1);
                SysBar.Value = Math.Round(sp * 100, 1);
                MixBar.Value = Math.Round(xp * 100, 1);

                MicDb.Text = $"{Dbfs.ToDbfs(mp),6:0.0} dBFS";
                SysDb.Text = $"{Dbfs.ToDbfs(sp),6:0.0} dBFS";
                MixDb.Text = $"{Dbfs.ToDbfs(xp),6:0.0} dBFS";

                UpdateRecordingElapsed();
                UpdateTransportState();

                var clip = _clipBrush ?? (System.Windows.Media.Brush)FindResource("ClipBrush");
                var off = _disabledBrush ?? (System.Windows.Media.Brush)FindResource("ControlDisabled");
                MicClip.Foreground = DateTime.Now < _micClipUntil ? clip : off;
                SysClip.Foreground = DateTime.Now < _sysClipUntil ? clip : off;
                MixClip.Foreground = DateTime.Now < _mixClipUntil ? clip : off;
            }
            catch { /* best-effort: meters must never break input */ }
        }

        /// <summary>
        /// Replaces the separate mini-bar: elapsed recording time stays in the
        /// main footer, next to the status text, and disappears when idle.
        /// </summary>
        private void UpdateRecordingElapsed()
        {
            if (!_engine.IsRecording)
            {
                if (ElapsedText.Visibility != Visibility.Collapsed)
                    ElapsedText.Visibility = Visibility.Collapsed;
                return;
            }

            var elapsed = _engine.RecordElapsed;
            ElapsedText.Text = elapsed.TotalHours >= 1
                ? elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
            if (ElapsedText.Visibility != Visibility.Visible)
                ElapsedText.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Pause/Stop are no-ops unless a file is actively recording and no
        /// auto stop/split owns the state transition; keep them disabled then.
        /// Runs from the 100 ms UI tick so engine-side stops (auto-stop, errors,
        /// encoding) disable the buttons even when no click initiated them.
        /// </summary>
        private void UpdateTransportState()
        {
            bool canControl = _engine.IsRecording && !_autoActionRunning;
            if (PauseBtn.IsEnabled != canControl) PauseBtn.IsEnabled = canControl;
            if (StopBtn.IsEnabled != canControl) StopBtn.IsEnabled = canControl;
            if (_trayPauseItem != null && _trayPauseItem.IsEnabled != canControl)
                _trayPauseItem.IsEnabled = canControl;
            if (_trayStopItem != null && _trayStopItem.IsEnabled != canControl)
                _trayStopItem.IsEnabled = canControl;
        }

        private void OnEngineEncodingProgress(object? sender, int percent)
        {
            // Encode loop fires per 64KB chunk; coalesce to one UI update per
            // percent point via async BeginInvoke so the UI never blocks the
            // encoder and the queue never floods (was sync Invoke per chunk).
            if (percent == _lastEncodePercent) return;
            _lastEncodePercent = percent;
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    EncodingProgressBar.Value = percent;
                    EncodingProgressText.Text = $"{percent}%";
                }
                catch { }
            });
        }

        private void OnEngineStatus(object? sender, EngineStatusEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = e.Message;

                if (e.Kind == EngineStatusKind.Encoding)
                {
                    _lastEncodePercent = 0;
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

                    // GAP-09 auto-split handoff: the next StartAsync rearms UI;
                    // skip the saved-dialog, folder open, and auto-monitor here.
                    if (_suppressStopUi) return;

                    if (_lastAutoReason != null)
                    {
                        ShowTrayBalloon("Hearbud: auto-stopped", $"{_lastAutoReason} — recording saved.");
                        _lastAutoReason = null;
                    }

                    StartBtn.IsEnabled = true;
                    StopBtn.IsEnabled = false;
                    PauseBtn.IsEnabled = false;
                    // Recording over: unfreeze source controls (an empty
                    // side stays locked off until devices come back).
                    ApplySideAvailability(_micDict.Count > 0, IncludeMicCheck, MicCombo);
                    ApplySideAvailability(_spkDict.Count > 0, RecordSysCheck, SpeakerCombo);
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
                    // Engine failed: restore source controls unless a
                    // recording is somehow still active (helper freezes).
                    ApplySideAvailability(_micDict.Count > 0, IncludeMicCheck, MicCombo);
                    ApplySideAvailability(_spkDict.Count > 0, RecordSysCheck, SpeakerCombo);
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
            long t0 = Stopwatch.GetTimestamp();
            CrashLog.LogOnly("UiInput",
                $"t={DateTime.Now:HH:mm:ss.fff} OnClosing entry recording={_engine.IsRecording} active={IsActive}");
            base.OnClosing(e);

            // GAP-09 #4: abort any pending start countdown.
            try { _countdownCts?.Cancel(); } catch { }

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
                    CrashLog.LogOnly("UiInput",
                        $"t={DateTime.Now:HH:mm:ss.fff} OnClosing cancelled +{Ms(t0):F0}ms");
                    return;
                }

                // Ensure the engine stops and flushes before the window fully closes
                await _engine.StopAsync();
                CrashLog.LogOnly("UiInput",
                    $"t={DateTime.Now:HH:mm:ss.fff} OnClosing StopAsync done +{Ms(t0):F0}ms");
                return;
            }

            CrashLog.LogOnly("UiInput",
                $"t={DateTime.Now:HH:mm:ss.fff} OnClosing done (idle) +{Ms(t0):F1}ms");
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Dispose();

            try
            {
                // Persist current UI selections (_settings already holds the
                // Settings-dialog values: bitrate, stems, limits, countdown).
                _settings.MicName = MicCombo.SelectedItem as string ?? _settings.MicName;
                _settings.SpeakerName = SpeakerCombo.SelectedItem as string ?? _settings.SpeakerName;
                _settings.OutputDir = OutputFolderText.Text;
                _settings.MicGain = MicGain.Value;
                _settings.LoopGain = LoopGain.Value;
                _settings.IncludeMic = IncludeMicCheck.IsChecked ?? true;
                _settings.IncludeSystem = RecordSysCheck.IsChecked ?? true;
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
            try { CompositionTarget.Rendering -= OnCompositionFrame; } catch { }
            try
            {
                // Click-glitch diagnostics: close summary with tail-capped input ring.
                try { _diagTimer?.Stop(); } catch { }
                string tail = string.Join(" | ", _diagInput.Tail(20));
                CrashLog.LogOnly("UiDiagnostics.Close",
                    $"ticks={_diagTickCount} maxLag={_diagLag.MaxLagMs:F0}ms spikes={_diagLag.SpikeCount} " +
                    $"mouseDown={_diagMouseDown} mouseUp={_diagMouseUp} stylus={_diagStylusDown} tail=[{tail}]");
                double firstFrameMs = _frameCount > 0 && _firstFrameTick != 0
                    ? (_firstFrameTick - _ctorTick) * 1000.0 / Stopwatch.Frequency
                    : -1;
                CrashLog.LogOnly("UiRender",
                    $"frames={_frameCount} firstFrameAfter={firstFrameMs:F0}ms " +
                    $"maxGap={(double)_maxFrameGapTicks * 1000.0 / Stopwatch.Frequency:F0}ms " +
                    $"size={ActualWidth:F0}x{ActualHeight:F0} state={WindowState} active={IsActive}");
            }
            catch { /* best-effort */ }
            try { _trayIcon?.Dispose(); } catch { }

            // Unregister device notifications before disposing the enumerator.
            try { _notifEnumerator?.UnregisterEndpointNotificationCallback(this); } catch { }
            try { _notifEnumerator?.Dispose(); } catch { }
            _notifEnumerator = null;

            _engine.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Click-glitch diagnostics: counts raw mouse/stylus presses + dropdown
        /// open/close into a capped ring, and runs a 1 Hz UI-thread lag watchdog
        /// (WARN to the log only on spikes &gt;250 ms, with tail context).
        /// </summary>
        private void SetupUiDiagnostics()
        {
            try
            {
                PreviewMouseDown += (_, e) =>
                {
                    _diagMouseDown++;
                    _diagInput.Record($"MouseDown src={UiDiagnostics.SourceName(e.OriginalSource)} tid={Environment.CurrentManagedThreadId}");
                };
                PreviewMouseUp += (_, e) =>
                {
                    _diagMouseUp++;
                    _diagInput.Record($"MouseUp src={UiDiagnostics.SourceName(e.OriginalSource)} tid={Environment.CurrentManagedThreadId}");
                };
                PreviewStylusDown += (_, e) =>
                {
                    _diagStylusDown++;
                    _diagInput.Record($"StylusDown src={UiDiagnostics.SourceName(e.OriginalSource)} tid={Environment.CurrentManagedThreadId}");
                };
                foreach (var cb in new System.Windows.Controls.ComboBox[] { MicCombo, SpeakerCombo })
                {
                    try
                    {
                        System.Windows.Controls.ComboBox captured = cb;
                        captured.DropDownOpened += (_, __) =>
                            _diagInput.Record($"DropDownOpened src={UiDiagnostics.SourceName(captured)} tid={Environment.CurrentManagedThreadId}");
                        captured.DropDownClosed += (_, __) =>
                            _diagInput.Record($"DropDownClosed src={UiDiagnostics.SourceName(captured)} tid={Environment.CurrentManagedThreadId}");
                    }
                    catch { /* one bad combo must not break the rest */ }
                }
                _diagTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _diagTimer.Tick += (_, __) => OnDiagTick();
            }
            catch { /* best-effort: window works without diagnostics */ }
        }

        private void OnDiagTick()
        {
            try
            {
                long now = Stopwatch.GetTimestamp();
                double elapsedMs = (now - _diagLastTick) * 1000.0 / Stopwatch.Frequency;
                _diagLastTick = now;
                _diagTickCount++;
                if (_diagTickCount <= 1) return; // baseline tick: no lag math yet
                double lag = _diagLag.Observe(elapsedMs, 1000.0);
                int dDown = _diagMouseDown - _diagLastMouseDown;
                int dUp = _diagMouseUp - _diagLastMouseUp;
                _diagLastMouseDown = _diagMouseDown;
                _diagLastMouseUp = _diagMouseUp;
                if (lag > 250.0)
                {
                    string tail = string.Join(" | ", _diagInput.Tail(10));
                    CrashLog.LogOnly("UiDiagnostics.Lag",
                        $"WARN lag={lag:F0}ms max={_diagLag.MaxLagMs:F0}ms spikes={_diagLag.SpikeCount} n={_diagTickCount} " +
                        $"mouseDown={_diagMouseDown}(+{dDown}) mouseUp={_diagMouseUp}(+{dUp}) stylus={_diagStylusDown} " +
                        $"tid={Environment.CurrentManagedThreadId} tail=[{tail}]");
                }
            }
            catch { /* best-effort */ }
        }

        /// <summary>Milliseconds elapsed since a <see cref="Stopwatch"/> tick.</summary>
        private static double Ms(long sinceTick)
            => (Stopwatch.GetTimestamp() - sinceTick) * 1000.0 / Stopwatch.Frequency;

        private void OnCompositionFrame(object? sender, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            if (_frameCount == 0)
            {
                _firstFrameTick = now;
            }
            else
            {
                long gap = now - _lastFrameTick;
                if (gap > _maxFrameGapTicks) _maxFrameGapTicks = gap;
            }
            _lastFrameTick = now;
            _frameCount++;
        }

        /// <summary>
        /// Win32 hook for the layers the WPF input ring cannot see: the
        /// non-client title bar (where the close button lives), activation,
        /// and WM_CLOSE itself. Transitions only, one line each.
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                _hwndSource = (HwndSource?)PresentationSource.FromVisual(this);
                _hwndSource?.AddHook(WndProcHook);
                CrashLog.LogOnly("UiInput",
                    $"t={DateTime.Now:HH:mm:ss.fff} SourceInitialized hwnd=0x{(_hwndSource?.Handle.ToInt64() ?? 0):X} after {Ms(_ctorTick):F0}ms tid={Environment.CurrentManagedThreadId}");
            }
            catch { /* best-effort */ }
        }

        private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            try
            {
                switch (msg)
                {
                    case 0x00A1: // WM_NCLBUTTONDOWN
                        CrashLog.LogOnly("UiInput",
                            $"t={DateTime.Now:HH:mm:ss.fff} WM_NCLBUTTONDOWN hit=0x{wParam.ToInt64():X} active={IsActive} state={WindowState}");
                        break;
                    case 0x00A2: // WM_NCLBUTTONUP
                        CrashLog.LogOnly("UiInput",
                            $"t={DateTime.Now:HH:mm:ss.fff} WM_NCLBUTTONUP hit=0x{wParam.ToInt64():X} active={IsActive}");
                        break;
                    case 0x0010: // WM_CLOSE
                        CrashLog.LogOnly("UiInput",
                            $"t={DateTime.Now:HH:mm:ss.fff} WM_CLOSE active={IsActive} recording={_engine.IsRecording}");
                        break;
                    case 0x0112: // WM_SYSCOMMAND
                        if ((wParam.ToInt64() & 0xFFF0) == 0xF060) // SC_CLOSE
                            CrashLog.LogOnly("UiInput",
                                $"t={DateTime.Now:HH:mm:ss.fff} WM_SYSCOMMAND SC_CLOSE active={IsActive}");
                        break;
                    case 0x0006: // WM_ACTIVATE
                        CrashLog.LogOnly("UiInput",
                            $"t={DateTime.Now:HH:mm:ss.fff} WM_ACTIVATE state=0x{wParam.ToInt64() & 0xFFFF:X}");
                        break;
                    case 0x0021: // WM_MOUSEACTIVATE
                        CrashLog.LogOnly("UiInput",
                            $"t={DateTime.Now:HH:mm:ss.fff} WM_MOUSEACTIVATE active={IsActive}");
                        break;
                }
            }
            catch { /* best-effort: diagnostics must never break input */ }
            return IntPtr.Zero;
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

                IncludeMicCheck.IsChecked = _settings.IncludeMic;
                RecordSysCheck.IsChecked = _settings.IncludeSystem;
                // An empty side stays locked off even if settings say
                // include (the startup refresh above already ran).
                ApplySideAvailability(_micDict.Count > 0, IncludeMicCheck, MicCombo);
                ApplySideAvailability(_spkDict.Count > 0, RecordSysCheck, SpeakerCombo);

                // Output format, stems, limits, countdown and hotkeys now live
                // in the Settings dialog and are already in _settings; just
                // rebuild the transport gestures from the persisted hotkeys.
                RebuildInputBindings();
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("LoadSettingsToUi", ex);
            }
        }

        /// <summary>
        /// GAP-09 #9 mapping (Settings-dialog owned): the single persisted
        /// value drives the engine. 0 = WAV master, otherwise WAV + MP3 at
        /// that bitrate (clamped to 64..320 for legacy configs).
        /// </summary>
        private int GetSelectedBitrateKbps()
        {
            int kb = _settings.Mp3BitrateKbps;
            if (kb == 0) return 0;
            return Math.Clamp(kb, 64, 320);
        }

        /// <summary>Opens the Settings dialog; rebuilds hotkey gestures on OK.</summary>
        private void OnOpenSettings(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new SettingsWindow(_settings) { Owner = this };
                if (dlg.ShowDialog() == true)
                    RebuildInputBindings();
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("OnOpenSettings", ex);
            }
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

        // Hotkey capture UI now lives in SettingsWindow; this window only
        // rebuilds the window-focused gestures from _settings.

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
                _trayPauseItem.IsEnabled = _engine.IsRecording && !_autoActionRunning;
            }
            if (_trayStopItem != null)
                _trayStopItem.IsEnabled = _engine.IsRecording && !_autoActionRunning;
        }

        /// <summary>GAP-09 #4: tray balloon tip (auto-stop / auto-split). Best-effort.</summary>
        private void ShowTrayBalloon(string title, string message)
        {
            try { _trayIcon?.ShowNotification(title, message, H.NotifyIcon.Core.NotificationIcon.Info); }
            catch { /* best-effort */ }
        }
    }
}
