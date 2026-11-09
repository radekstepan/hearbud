using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Hearbud
{
    /// <summary>
    /// Advanced recording settings moved out of the main window to keep it
    /// compact. Edits the live <see cref="AppSettings"/> in place; OK persists
    /// it, Cancel restores the snapshot taken at open.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly double _origMinutes;
        private readonly double _origSizeMb;
        private readonly bool _origSplit;
        private readonly int _origCountdown;
        private readonly int _origBitrate;
        private readonly bool _origStems;
        private readonly string _origStart;
        private readonly string _origPause;
        private readonly string _origStop;

        public SettingsWindow(AppSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _origMinutes = settings.AutoStopMinutes;
            _origSizeMb = settings.AutoStopSizeMb;
            _origSplit = settings.SplitInsteadOfStop;
            _origCountdown = settings.CountdownSeconds;
            _origBitrate = settings.Mp3BitrateKbps;
            _origStems = settings.WriteStems;
            _origStart = settings.StartHotkey;
            _origPause = settings.PauseHotkey;
            _origStop = settings.StopHotkey;

            InitializeComponent();
            LoadFromSettings();
        }

        private void LoadFromSettings()
        {
            bool wavOnly = _settings.Mp3BitrateKbps == 0;
            OutputFormatCombo.SelectedValue = wavOnly ? "wav" : "mp3";
            if (OutputFormatCombo.SelectedItem as ComboBoxItem == null)
                OutputFormatCombo.SelectedValue = "mp3";
            string bitrateTag = wavOnly ? "192" : _settings.Mp3BitrateKbps.ToString(CultureInfo.InvariantCulture);
            OutputQualityCombo.SelectedValue = bitrateTag;
            if (OutputQualityCombo.SelectedItem as ComboBoxItem == null)
                OutputQualityCombo.SelectedValue = "192";
            UpdateBitrateEnabled();

            WriteStemsCheck.IsChecked = _settings.WriteStems;
            LimitMinutesText.Text = _settings.AutoStopMinutes > 0 ? _settings.AutoStopMinutes.ToString(CultureInfo.InvariantCulture) : "";
            LimitSizeMbText.Text = _settings.AutoStopSizeMb > 0 ? _settings.AutoStopSizeMb.ToString(CultureInfo.InvariantCulture) : "";
            SplitInsteadOfStopCheck.IsChecked = _settings.SplitInsteadOfStop;
            CountdownCombo.SelectedValue = _settings.CountdownSeconds switch { 3 => "3", 5 => "5", 10 => "10", _ => "0" };

            StartHotkeyBox.Text = _settings.StartHotkey;
            PauseHotkeyBox.Text = _settings.PauseHotkey;
            StopHotkeyBox.Text = _settings.StopHotkey;
        }

        private bool IsMp3Included()
        {
            try
            {
                string? tag = (OutputFormatCombo.SelectedItem as ComboBoxItem)?.Tag as string
                    ?? OutputFormatCombo.SelectedValue as string;
                if (string.Equals(tag, "wav", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(tag, "mp3", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return true;
        }

        private void UpdateBitrateEnabled()
        {
            try
            {
                if (OutputQualityCombo != null)
                    OutputQualityCombo.IsEnabled = IsMp3Included();
            }
            catch { }
        }

        private void OnOutputFormatChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateBitrateEnabled();
        }

        private int GetSelectedBitrateKbps()
        {
            try
            {
                if (!IsMp3Included()) return 0;
                int kb = 192;
                if (OutputQualityCombo.SelectedItem is ComboBoxItem item &&
                    item.Tag is string s && int.TryParse(s, out var parsedKb))
                    kb = parsedKb;
                else if (OutputQualityCombo.SelectedValue is string kbpsStr && int.TryParse(kbpsStr, out var kbpsParsed))
                    kb = kbpsParsed;
                if (kb == 0) return 0;
                return Math.Clamp(kb, 64, 320);
            }
            catch { }
            return 192;
        }

        private static double ParseNonNegativeMinutes(string? text)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) && v > 0)
                return Math.Min(v, 24 * 60);
            if (double.TryParse(text, out v) && !double.IsNaN(v) && v > 0)
                return Math.Min(v, 24 * 60);
            return 0;
        }

        private static double ParseNonNegativeSizeMb(string? text)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) && v > 0)
                return Math.Min(v, 100_000);
            if (double.TryParse(text, out v) && !double.IsNaN(v) && v > 0)
                return Math.Min(v, 100_000);
            return 0;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            _settings.Mp3BitrateKbps = GetSelectedBitrateKbps();
            _settings.WriteStems = WriteStemsCheck.IsChecked ?? true;
            _settings.AutoStopMinutes = ParseNonNegativeMinutes(LimitMinutesText.Text);
            _settings.AutoStopSizeMb = ParseNonNegativeSizeMb(LimitSizeMbText.Text);
            _settings.SplitInsteadOfStop = SplitInsteadOfStopCheck.IsChecked ?? false;
            string? tag = (CountdownCombo.SelectedItem as ComboBoxItem)?.Tag as string
                ?? CountdownCombo.SelectedValue as string;
            _settings.CountdownSeconds = tag switch { "3" => 3, "5" => 5, "10" => 10, _ => 0 };
            _settings.Save();
            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            _settings.AutoStopMinutes = _origMinutes;
            _settings.AutoStopSizeMb = _origSizeMb;
            _settings.SplitInsteadOfStop = _origSplit;
            _settings.CountdownSeconds = _origCountdown;
            _settings.Mp3BitrateKbps = _origBitrate;
            _settings.WriteStems = _origStems;
            _settings.StartHotkey = _origStart;
            _settings.PauseHotkey = _origPause;
            _settings.StopHotkey = _origStop;
            DialogResult = false;
            Close();
        }

        private void OnHotkeyBoxGotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.TextBox tb)
                tb.SelectAll();
        }

        private void OnHotkeyBoxLostFocus(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is System.Windows.Controls.TextBox tb && tb.Tag is string tag)
                    tb.Text = HotkeyFor(tag);
            }
            catch { }
        }

        private string HotkeyFor(string tag) => tag switch
        {
            "Start" => _settings.StartHotkey,
            "Pause" => _settings.PauseHotkey,
            "Stop" => _settings.StopHotkey,
            _ => "",
        };

        private void OnHotkeyCapturePreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (sender is not System.Windows.Controls.TextBox tb || tb.Tag is not string tag)
                return;
            try
            {
                e.Handled = true;
                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.ImeProcessed)
                    key = e.ImeProcessedKey;
                if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                    or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
                    return;
                if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
                {
                    tb.Text = HotkeyFor(tag);
                    MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    return;
                }
                var mods = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows);
                if (mods == ModifierKeys.None)
                    return;
                string? gestureText;
                try
                {
                    gestureText = new KeyGestureConverter().ConvertToString(new KeyGesture(key, mods));
                }
                catch
                {
                    return;
                }
                if (string.IsNullOrWhiteSpace(gestureText) ||
                    !AppSettings.TryNormalizeHotkey(gestureText, out var normalized))
                    return;
                string other1, other2;
                if (tag == "Start") { other1 = _settings.PauseHotkey; other2 = _settings.StopHotkey; }
                else if (tag == "Pause") { other1 = _settings.StartHotkey; other2 = _settings.StopHotkey; }
                else if (tag == "Stop") { other1 = _settings.StartHotkey; other2 = _settings.PauseHotkey; }
                else return;
                if (string.Equals(normalized, other1, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalized, other2, StringComparison.OrdinalIgnoreCase))
                {
                    System.Windows.MessageBox.Show(
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
                StartHotkeyBox.Text = _settings.StartHotkey;
                PauseHotkeyBox.Text = _settings.PauseHotkey;
                StopHotkeyBox.Text = _settings.StopHotkey;
            }
            catch (Exception ex)
            {
                CrashLog.LogAndShow("OnResetHotkeys", ex);
            }
        }
    }
}
