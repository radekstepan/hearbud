using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Hearbud
{
    public partial class MainWindow : Window
    {
        // The engine uses AudioCapture.Default, which Program.Main points at
        // the PortAudio factory on macOS (WASAPI elsewhere) before startup.
        private readonly RecorderEngine _engine = new();
        private readonly System.Timers.Timer _uiTimer = new(100);
        private TrayIcon? _trayIcon;

        private AppSettings _settings = AppSettings.Load();
        // Phase 4: the engine owns all native audio objects; the UI only holds name→id maps.
        private readonly Dictionary<string, string> _micDict = new();
        private readonly Dictionary<string, string> _spkDict = new();
        // macOS Output entry for sessions without system audio (empty id =
        // the engine skips the loop leg). ASCII by design: it round-trips
        // through settings, logs, and the tray tooltip.
        private const string MicOnlyOutputName = "(Mic only - no system audio)";

        private bool _uiReady;
        private bool _forceClose;

        // Cached meter brushes (Avalonia resolves these from app resources).
        private IBrush _clipBrush = Brushes.Red;
        private IBrush _disabledBrush = Brushes.Gray;

        // Phase 1 (v2 supervise): latest 1 Hz engine health, for tray/meters.
        private HealthReport? _latestHealth;
        private bool _humShown;

        public MainWindow()
        {
            try
            {
                InitializeComponent();

                _clipBrush = GetAppBrush("ClipBrush", Brushes.Red);
                _disabledBrush = GetAppBrush("ControlDisabled", Brushes.Gray);

                // App icon (tray + window) from the embedded asset.
                var icon = LoadAppIcon();
                if (icon != null) Icon = icon;

                // Shortcuts (Ctrl+R start, Ctrl+S stop)
                KeyBindings.Add(new KeyBinding
                {
                    Gesture = new KeyGesture(Key.R, KeyModifiers.Control),
                    Command = new RelayCommand(() => OnStart(null, null))
                });
                KeyBindings.Add(new KeyBinding
                {
                    Gesture = new KeyGesture(Key.S, KeyModifiers.Control),
                    Command = new RelayCommand(() => OnStop(null, null))
                });

                // Setup system tray icon
                SetupTrayIcon(icon);

                Loaded += async (_, __) =>
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
                            await MessageDialog.ShowAsync(this,
                                $"Recovered {recovered.Length} unfinished take(s) to:\n{sweepDir}",
                                "Recovered recordings");
                        }
                    }
                    catch { /* the sweep must never break startup */ }

                    // Auto-start live monitoring so meters show immediately
                    TryStartAutoMonitor();

                    // Phase 4: device notifications are engine-owned (it also
                    // auto-recovers capture legs); the UI just refreshes lists.
                    // Engine events arrive on a backend thread — marshal to the UI.
                    _engine.DevicesChanged += (_, __) => Dispatcher.UIThread.Post(() =>
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

                _uiTimer.Elapsed += (_, __) => Dispatcher.UIThread.Invoke(UpdateMeters);
                _uiTimer.Start();
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("MainWindow.ctor", ex);
                throw;
            }
        }

        // NOTE: no manual InitializeComponent — the Avalonia compiler generates
        // it, wiring x:Name fields. A manual AvaloniaXamlLoader.Load version
        // would leave those fields null (runtime-only namescope).

        private static IBrush GetAppBrush(string key, IBrush fallback)
        {
            try
            {
                if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is IBrush b)
                    return b;
            }
            catch { /* fall through to the fallback */ }
            return fallback;
        }

        private static WindowIcon? LoadAppIcon()
        {
            try
            {
                using var s = AssetLoader.Open(new Uri("avares://Hearbud.Avalonia/Assets/icon.png"));
                return new WindowIcon(s);
            }
            catch
            {
                return null;
            }
        }

        private void OnRefreshDevices(object? sender, RoutedEventArgs e)
        {
            SafeRefreshDevices();
            TryStartAutoMonitor(); // keep meters alive after device changes
        }

        private void SafeRefreshDevices()
        {
            try
            {
                _micDict.Clear(); _spkDict.Clear();

                // The statics follow AudioCapture.Default, which points at the
                // PortAudio factory on macOS (see Program.Main).
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
                var spkNames = _spkDict.Keys.ToList();
                if (OperatingSystem.IsMacOS())
                {
                    // Without a virtual driver every entry is a mic, so offer
                    // an explicit mic-only choice first instead of ever
                    // defaulting the Output box to a microphone.
                    _spkDict[MicOnlyOutputName] = "";
                    spkNames.Insert(0, MicOnlyOutputName);
                }
                SpeakerCombo.ItemsSource = spkNames;

                if (defMic != null && _micDict.ContainsKey(defMic))
                    MicCombo.SelectedItem = defMic;
                if (defSpk != null && _spkDict.ContainsKey(defSpk))
                    SpeakerCombo.SelectedItem = defSpk;

                if (MicCombo.SelectedItem == null && _micDict.Count > 0)
                    MicCombo.SelectedItem = _micDict.Keys.First();
                if (SpeakerCombo.SelectedItem == null && spkNames.Count > 0)
                    SpeakerCombo.SelectedItem = OperatingSystem.IsMacOS() ? MicOnlyOutputName : spkNames[0];

                // macOS has no system-audio tap: without a virtual driver the
                // output list holds only mics, so explain the setup inline.
                BlackHoleHint.IsVisible = OperatingSystem.IsMacOS() &&
                    !_spkDict.Keys.Any(PortAudioCaptureFactory.IsVirtualLoopbackDevice);

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

        // Best-effort wrapper for BACKGROUND monitoring: failures go to the log
        // plus the status line, never a modal crash dialog (explicit Start
        // keeps its own dialog). The first line of backend errors is written
        // to be user-actionable.
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
                    CrashLog.Log("Device retry", retryEx);
                    StatusText.Text = "Monitoring unavailable: " + FirstLine(retryEx.Message);
                }
            }
            catch (Exception ex)
            {
                CrashLog.Log("ExecuteWithDeviceRetry", ex);
                StatusText.Text = "Monitoring unavailable: " + FirstLine(ex.Message);
            }
        }

        private static string FirstLine(string message)
        {
            int nl = message.IndexOfAny(new[] { '\r', '\n' });
            string first = (nl < 0 ? message : message[..nl]).Trim();
            return first.Length <= 160 ? first : first[..157] + "...";
        }

        private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
        {
            try
            {
                IStorageFolder? start = null;
                try
                {
                    var current = OutputFolderText.Text;
                    if (!string.IsNullOrWhiteSpace(current))
                        start = await StorageProvider.TryGetFolderFromPathAsync(new Uri(current));
                    start ??= await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Music);
                }
                catch { /* fall back to the provider default location */ }

                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select output folder",
                    SuggestedStartLocation = start,
                    AllowMultiple = false
                });
                if (folders.Count > 0)
                {
                    OutputFolderText.Text = folders[0].Path.LocalPath;
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

                var baseName = SanitizeFilename(BaseNameText.Text ?? "");
                if (string.IsNullOrWhiteSpace(baseName))
                {
                    baseName = $"rec-{DateTime.Now:yyyyMMdd_HHmmss}";
                    BaseNameText.Text = baseName;
                }

                // Validate total path length to avoid Windows MAX_PATH (260) exceptions.
                // We check against 240 to leave room for internal suffixes like -system.wav or -mic.wav.
                // Windows-only: macOS/Linux allow much longer paths.
                var testPath = Path.Combine(outDir, $"{baseName}-system.wav");
                if (OperatingSystem.IsWindows() && testPath.Length > 240)
                {
                    await MessageDialog.ShowAsync(this,
                        "Output path is too long. Please use a shorter folder path or file name.",
                        "Path Too Long");
                    return;
                }

                var micName = MicCombo.SelectedItem as string;
                var spkName = SpeakerCombo.SelectedItem as string;

                if (spkName == null)
                {
                    await MessageDialog.ShowAsync(this,
                        "Select an output device to capture.", "Missing device");
                    return;
                }
                if (micName == null)
                {
                    await MessageDialog.ShowAsync(this,
                        "Select a microphone.", "Missing device");
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

        private void OnGainChanged(object? sender, RangeBaseValueChangedEventArgs e)
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

            MicClip.Foreground = DateTime.Now < _micClipUntil ? _clipBrush : _disabledBrush;
            SysClip.Foreground = DateTime.Now < _sysClipUntil ? _clipBrush : _disabledBrush;
        }

        // Phase 1 (v2 supervise): stash the 1 Hz health report for the tray tooltip
        // and surface mains hum in the status line while it persists.
        private void OnEngineHealth(object? sender, HealthReport e)
        {
            _latestHealth = e;
            Dispatcher.UIThread.Invoke(() =>
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
            Dispatcher.UIThread.Invoke(() =>
            {
                EncodingProgressBar.Value = percent;
                EncodingProgressText.Text = $"{percent}%";
            });
        }

        private void OnEngineStatus(object? sender, EngineStatusEventArgs e)
        {
            // Async marshal: engine callbacks arrive on backend threads and the
            // Avalonia dialogs are awaitable, so the whole branch runs as one
            // ordered UI-thread continuation (WPF blocked synchronously here).
            Dispatcher.UIThread.Post(async () =>
            {
                StatusText.Text = e.Message;

                if (e.Kind == EngineStatusKind.Encoding)
                {
                    EncodingProgressBar.IsVisible = true;
                    EncodingProgressText.IsVisible = true;
                    EncodingProgressBar.Value = 0;
                    EncodingProgressText.Text = "0%";
                }
                else if (e.Kind == EngineStatusKind.Stopped)
                {
                    EncodingProgressBar.IsVisible = false;
                    EncodingProgressText.IsVisible = false;

                    StartBtn.IsEnabled = true;
                    StopBtn.IsEnabled = false;
                    UpdateTrayTooltip();

                    await MessageDialog.ShowAsync(this,
                        $"Saved files to:\n{Path.GetDirectoryName(e.OutputPathSystem)}",
                        "Files saved");

                    // Open the output folder
                    try
                    {
                        string? directoryPath = Path.GetDirectoryName(e.OutputPathSystem);
                        if (!string.IsNullOrEmpty(directoryPath) && Directory.Exists(directoryPath))
                        {
                            var psi = new ProcessStartInfo()
                            {
                                FileName = directoryPath,
                                UseShellExecute = true
                            };
                            if (OperatingSystem.IsWindows()) psi.Verb = "open";
                            Process.Start(psi);
                        }
                    }
                    catch (Exception ex) { CrashLog.LogAndShow("OpenOutputFolder", ex); }

                    // Keep monitoring so meters remain live after stop
                    TryStartAutoMonitor();
                }
                else if (e.Kind == EngineStatusKind.Error)
                {
                    await MessageDialog.ShowAsync(this, e.Message, "Recording Error");
                }
            });
        }

        private void SetupTrayIcon(WindowIcon? icon)
        {
            try
            {
                var menu = new NativeMenu();
                var showItem = new NativeMenuItem
                {
                    Header = "Show Window",
                    Command = new RelayCommand(() =>
                    {
                        Show();
                        WindowState = WindowState.Normal;
                        Activate();
                    })
                };
                var exitItem = new NativeMenuItem
                {
                    Header = "Exit",
                    Command = new RelayCommand(() =>
                    {
                        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                            lifetime.Shutdown();
                    })
                };
                menu.Items.Add(showItem);
                menu.Items.Add(exitItem);

                var tray = new TrayIcon();
                // macOS crashes on a null tooltip: set ToolTipText (and the
                // icon/menu) before flipping IsVisible.
                tray.ToolTipText = "Hearbud - Audio Recorder";
                if (icon != null) tray.Icon = icon;
                tray.Menu = menu;
                tray.Clicked += (_, __) =>
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
                tray.IsVisible = true;

                TrayIcon.GetIcons(Application.Current!)?.Add(tray);
                _trayIcon = tray;
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("SetupTrayIcon", ex);
            }
        }

        // NOTE: WPF's no-op OnStateChanged override has no Avalonia counterpart
        // and is dropped.

        /// <summary>
        /// Handles the window closing event. If a recording is in progress,
        /// prompts the user for confirmation to prevent accidental data loss.
        /// The Avalonia dialog is async, so the first close is cancelled and
        /// re-issued after confirmation.
        /// </summary>
        protected override async void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);

            if (_forceClose) return;

            if (_engine.IsRecording)
            {
                e.Cancel = true;

                bool stop;
                try
                {
                    stop = await MessageDialog.ShowConfirmAsync(this,
                        "Recording is in progress. Stop recording and exit?",
                        "Confirm Exit");
                }
                catch
                {
                    return;
                }
                if (!stop) return;

                // Ensure the engine stops and flushes before the window fully closes
                try
                {
                    await _engine.StopAsync();
                }
                catch (Exception ex)
                {
                    CrashLog.LogAndShow("OnClosing.Stop", ex);
                }

                _forceClose = true;
                Close();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try { _uiTimer.Stop(); } catch { }
            try
            {
                if (_trayIcon != null)
                {
                    TrayIcon.GetIcons(Application.Current!)?.Remove(_trayIcon);
                    _trayIcon.Dispose();
                    _trayIcon = null;
                }
            }
            catch { }

            // (Phase 4: device notifications are engine-owned; _engine.Dispose() stops them.)

            try
            {
                // Persist current UI selections
                _settings.MicName = MicCombo.SelectedItem as string ?? _settings.MicName;
                _settings.SpeakerName = SpeakerCombo.SelectedItem as string ?? _settings.SpeakerName;
                _settings.OutputDir = OutputFolderText.Text ?? _settings.OutputDir;
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
                if (!string.IsNullOrWhiteSpace(_settings.SpeakerName) && _spkDict.ContainsKey(_settings.SpeakerName)
                    && IsOutputRestoreAllowed(_settings.SpeakerName))
                    SpeakerCombo.SelectedItem = _settings.SpeakerName;

                MicGain.Value  = _settings.MicGain;
                LoopGain.Value = _settings.LoopGain;

                // Select Output Quality (0 for WAV, else MP3 bitrate) by
                // scanning item Tags (Avalonia 11.3 has no SelectedValuePath).
                var desired = _settings.Mp3BitrateKbps < 0 ? 192 : _settings.Mp3BitrateKbps;
                SelectQualityByTag(desired.ToString());

                // If desired value isn't in the list, fall back to 192.
                if (OutputQualityCombo.SelectedItem as ComboBoxItem == null)
                    SelectQualityByTag("192");
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("LoadSettingsToUi", ex);
            }
        }

        // macOS Output restore filter: a saved mic name is never a valid
        // system-audio source (earlier builds auto-saved the wrong default),
        // so only the mic-only entry or a virtual driver restores — anything
        // else falls through to the auto-default. Windows restores anything.
        private static bool IsOutputRestoreAllowed(string name) =>
            !OperatingSystem.IsMacOS()
            || name == MicOnlyOutputName
            || PortAudioCaptureFactory.IsVirtualLoopbackDevice(name);

        private void SelectQualityByTag(string tag)
        {
            foreach (var obj in OutputQualityCombo.Items)
            {
                if (obj is ComboBoxItem ci && ci.Tag is string s && s == tag)
                {
                    OutputQualityCombo.SelectedItem = ci;
                    return;
                }
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

            var health = _latestHealth;
            string suffix = health == null ? ""
                : health.HumDetected ? $" · hum @{health.HumFreqHz}Hz"
                : health.DroppedBlocks > 0 ? $" · {health.DroppedBlocks} dropped"
                : "";

            _trayIcon.ToolTipText = (_engine.IsRecording
                ? $"● Recording — {spkName ?? "No device"}"
                : spkName != null
                    ? $"Hearbud — {spkName}"
                    : "Hearbud — No device connected") + suffix;
        }

        // Minimal ICommand for tray menu items and key bindings (Avalonia has no
        // built-in delegate command without the ReactiveUI package).
        private sealed class RelayCommand : ICommand
        {
            private readonly Action _execute;
            public RelayCommand(Action execute) => _execute = execute;
            public event EventHandler? CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => _execute();
        }
    }
}
