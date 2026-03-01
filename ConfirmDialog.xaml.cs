using System.Windows;
using System.Windows.Media;

namespace BoltFetch
{
    public partial class ConfirmDialog : Window
    {
        public bool Confirmed { get; private set; } = false;

        public ConfirmDialog(string title, string message, string confirmText = "Confirm", bool isDanger = false)
        {
            InitializeComponent();
            TitleText.Text = title;
            MessageText.Text = message;
            ConfirmBtn.Content = confirmText;
            IconText.Text = isDanger ? "🗑️" : "❓";
            ConfirmBtn.Background = isDanger 
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26)) 
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0xBE, 0xF9));
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            Close();
        }

        public static bool Show(Window owner, string title, string message, string confirmText = "Confirm", bool isDanger = false)
        {
            var dialog = new ConfirmDialog(title, message, confirmText, isDanger);
            dialog.Owner = owner;
            dialog.ShowDialog();
            return dialog.Confirmed;
        }

        private void Window_Deactivated(object sender, System.EventArgs e)
        {
            try { Close(); } catch { }
        }
    }
}
