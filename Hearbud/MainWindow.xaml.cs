using CSCore.CoreAudioAPI;
using System;
using System.Collections.Generic;
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

namespace Hearbud
{
    public partial class MainWindow : Window
    {
    private readonly RecorderEngine _engine = new();
    private readonly System.Timers.Timer _uiTimer = new(100);

    private AppSettings _settings = AppSettings.Load();
        private readonly Dictionary<string, MMDevice> _micDict = new();
        private readonly Dictionary<string, MMDevice> _spkDict = new();

        private bool _uiReady;

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
                };

                _engine.LevelChanged += OnEngineLevelChanged;
                _engine.Status += OnEngineStatus;

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

        private void TryStartAutoMonitor()
        {
            try
            {
                var micName = MicCombo.SelectedItem as string;
                var spkName = SpeakerCombo.SelectedItem as string;

                if (spkName == null || micName == null) return;

                MMDevice? loopDevice = _spkDict[spkName];

                _engine.MicGain = MicGain.Value;
                _engine.LoopGain = LoopGain.Value;

                _engine.Monitor(new RecorderStartOptions
                {
                    LoopbackDeviceId = loopDevice!.DeviceID,
                    MicDeviceId = _micDict[micName].DeviceID
                });

                StatusText.Text = "Monitoring...";
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("TryStartAutoMonitor", ex);
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

        private void OnStart(object? sender, RoutedEventArgs? e)
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

                var baseName = BaseNameText.Text.Trim();
                if (string.IsNullOrWhiteSpace(baseName))
                {
                    baseName = $"rec-{DateTime.Now:yyyyMMdd_HHmmss}";
                    BaseNameText.Text = baseName;
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

                MMDevice? loopDevice = _spkDict[spkName];

                _engine.MicGain = MicGain.Value;   // meters & mix balance
                _engine.LoopGain = LoopGain.Value; // meters & mix balance

                _engine.Start(new RecorderStartOptions
                {
                    OutputPath = Path.Combine(outDir, baseName),
                    LoopbackDeviceId = loopDevice!.DeviceID,
                    MicDeviceId = _micDict[micName].DeviceID,
                    Mp3BitrateKbps = GetSelectedBitrateKbps()
                });

                StartBtn.IsEnabled = false;
                StopBtn.IsEnabled  = true;
                StatusText.Text = "Recording...";
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("OnStart", ex);
            }
        }

        private void OnStop(object? sender, RoutedEventArgs? e)
        {
            try { _engine.Stop(); }
            catch (Exception ex) { CrashLog.LogAndShow("OnStop", ex); }
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

        private void OnEngineLevelChanged(object? sender, LevelChangedEventArgs e)
        {
            if (e.Source == LevelSource.Mic) _micPeak = e.Peak;
            else _sysPeak = e.Peak;

            if (e.Clipped)
            {
                Dispatcher.Invoke(() =>
                {
                    var tb = e.Source == LevelSource.Mic ? MicClip : SysClip;
                    tb.Foreground = (System.Windows.Media.Brush)FindResource("ClipBrush");
                    _ = new System.Threading.Timer(_ =>
                    {
                        Dispatcher.Invoke(() => tb.Foreground = (System.Windows.Media.Brush)FindResource("ControlDisabled"));
                    }, null, 1500, System.Threading.Timeout.Infinite);
                });
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
        }

        private void OnEngineStatus(object? sender, EngineStatusEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = e.Message;
                if (e.Kind == EngineStatusKind.Stopped)
                {
                    StartBtn.IsEnabled = true;
                    StopBtn.IsEnabled = false;

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

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try { _uiTimer.Stop(); } catch { }
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

                // Select MP3 bitrate (default 192 if not found)
                var desired = _settings.Mp3BitrateKbps <= 0 ? 192 : _settings.Mp3BitrateKbps;
                Mp3BitrateCombo.SelectedValue = desired;
                // If desired not in the list (custom), fall back to 192
                if ((Mp3BitrateCombo.SelectedItem as ComboBoxItem) == null)
                    Mp3BitrateCombo.SelectedValue = 192;
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
                if (Mp3BitrateCombo.SelectedItem is ComboBoxItem item &&
                    item.Tag is string s &&
                    int.TryParse(s, out var kb))
                {
                    return Math.Clamp(kb, 64, 320);
                }
                if (Mp3BitrateCombo.SelectedValue is int kbpsInt)
                    return Math.Clamp(kbpsInt, 64, 320);
                if (Mp3BitrateCombo.SelectedValue is string kbpsStr && int.TryParse(kbpsStr, out var kbpsParsed))
                    return Math.Clamp(kbpsParsed, 64, 320);
            }
            catch { }
            return 192;
        }
    }
}
