using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Hearbud
{
    // Avalonia has no built-in MessageBox: one small modal window replaces all
    // 7 WPF MessageBox sites (OK info/warning/error + Yes/No confirm).
    public partial class MessageDialog : Window
    {
        // NOTE: no manual InitializeComponent — the Avalonia compiler generates
        // it, wiring x:Name fields (see MainWindow).
        public MessageDialog()
        {
            InitializeComponent();
        }

        public static async Task ShowAsync(Window owner, string message, string title)
        {
            var dlg = new MessageDialog { Title = title };
            dlg.MessageText.Text = message;
            dlg.OkButton.IsVisible = true;
            dlg.YesButton.IsVisible = false;
            dlg.NoButton.IsVisible = false;
            await dlg.ShowDialog(owner);
        }

        public static async Task<bool> ShowConfirmAsync(Window owner, string message, string title)
        {
            var dlg = new MessageDialog { Title = title };
            dlg.MessageText.Text = message;
            dlg.OkButton.IsVisible = false;
            dlg.YesButton.IsVisible = true;
            dlg.NoButton.IsVisible = true;
            // Closing via the X button yields default(bool) == false (No).
            return await dlg.ShowDialog<bool>(owner);
        }

        private void OnOk(object? sender, RoutedEventArgs e) => Close();
        private void OnYes(object? sender, RoutedEventArgs e) => Close(true);
        private void OnNo(object? sender, RoutedEventArgs e) => Close(false);
    }
}
