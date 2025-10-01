// file: WasapiLoopMix/MainWindow.xaml.cs
using CSCore.CoreAudioAPI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

using WpfMessageBox = System.Windows.MessageBox;
using WinFormsFolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;

namespace WasapiLoopMix
{
    public partial class MainWindow : Window
    {
        private readonly RecorderEngine _engine = new();
        private readonly System.Timers.Timer _uiTimer = new(100);

        private AppSettings _settings = AppSettings.Load();
        private readonly Dictionary<string, MMDevice> _micDict = new();
        private readonly Dictionary<string, MMDevice> _spkDict = new();
        private readonly Dictionary<string, MMDevice> _loopDict = new();

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
                _micDict.Clear(); _spkDict.Clear(); _loopDict.Clear();

                using var enumerator = new MMDeviceEnumerator();

                foreach (var dev in enumerator.EnumAudioEndpoints(DataFlow.Capture, DeviceState.Active))
                    _micDict[dev.FriendlyName] = dev;

                foreach (var dev in enumerator.EnumAudioEndpoints(DataFlow.Render, DeviceState.Active))
                {
                    _spkDict[dev.FriendlyName] = dev;
                    _loopDict[dev.FriendlyName] = dev; // same render endpoint for loopback
                }

                MicCombo.ItemsSource = _micDict.Keys.ToList();
                SpeakerCombo.ItemsSource = _spkDict.Keys.ToList();

                var lb = new List<string> { "Auto (match speaker)" };
                lb.AddRange(_loopDict.Keys);
                LoopbackCombo.ItemsSource = lb;

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
                if (LoopbackCombo.Items.Count > 0 && LoopbackCombo.SelectedItem == null)
                    LoopbackCombo.SelectedIndex = 0;

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
                var lbName  = LoopbackCombo.SelectedItem as string;

                if ((spkName == null && lbName == "Auto (match speaker)") && (lbName == null)) return;
                if (micName == null) return;

                MMDevice? loopDevice = null;
                if (lbName != null && lbName != "Auto (match speaker)") loopDevice = _loopDict[lbName];
                else if (spkName != null) loopDevice = _spkDict[spkName];

                _engine.MicGain = MicGain.Value;
                _engine.LoopGain = LoopGain.Value;

                _engine.Monitor(new RecorderStartOptions
                {
                    LoopbackDeviceId = loopDevice!.DeviceID,
                    MicDeviceId = _micDict[micName].DeviceID
                });

                StatusText.Text = "Live meters active (monitoring).";
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
                var lbName  = LoopbackCombo.SelectedItem as string;

                if ((spkName == null && lbName == "Auto (match speaker)") && (lbName == null))
                {
                    WpfMessageBox.Show("Select an output/loopback device.", "Missing device",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (micName == null)
                {
                    WpfMessageBox.Show("Select a microphone.", "Missing device",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                MMDevice? loopDevice = null;
                if (lbName != null && lbName != "Auto (match speaker)") loopDevice = _loopDict[lbName];
                else if (spkName != null) loopDevice = _spkDict[spkName];

                _engine.MicGain = MicGain.Value;   // meters & mix balance
                _engine.LoopGain = LoopGain.Value; // meters & mix balance

                _engine.Start(new RecorderStartOptions
                {
                    OutputPath = Path.Combine(outDir, baseName),
                    LoopbackDeviceId = loopDevice!.DeviceID,
                    MicDeviceId = _micDict[micName].DeviceID,
                    Mp3BitrateKbps = 192
                });

                StartBtn.IsEnabled = false;
                StopBtn.IsEnabled  = true;
                StatusText.Text = "Recording to WAV (system, mic, mix) + MP3 (mix)…";
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
                    tb.Foreground = System.Windows.Media.Brushes.IndianRed;
                    _ = new System.Threading.Timer(_ =>
                    {
                        Dispatcher.Invoke(() => tb.Foreground = System.Windows.Media.Brushes.Gray);
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

                    // ALWAYS show the saved message (even if some files are empty / mic-only)
                    WpfMessageBox.Show(
                        $"Saved:\n{e.OutputPathSystem}\n{e.OutputPathMic}\n{e.OutputPathMix}\n{e.OutputPathMp3}",
                        "Files saved",
                        MessageBoxButton.OK, MessageBoxImage.Information);

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
                if (!string.IsNullOrWhiteSpace(_settings.LoopbackName))
                    LoopbackCombo.SelectedItem = _settings.LoopbackName;

                MicGain.Value  = _settings.MicGain;
                LoopGain.Value = _settings.LoopGain;
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("LoadSettingsToUi", ex);
            }
        }
    }
}
