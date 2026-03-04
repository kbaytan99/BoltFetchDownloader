using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;

namespace BoltFetch.Views
{
    public partial class AddLinkWindow : Window
    {
        public List<string> Links { get; private set; } = new List<string>();

        public AddLinkWindow()
        {
            InitializeComponent();
            LinksTextBox.Focus();
        }

        private void PasteButton_Click(object sender, RoutedEventArgs e)
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                LinksTextBox.AppendText(System.Windows.Clipboard.GetText() + "\n");
            }
        }

        private void Window_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                this.DragMove();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var text = LinksTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(LinksTextBox.Text)) // Changed LinksInput.Text to LinksTextBox.Text to match existing control name
            {
                var errorWin = new GlobalErrorWindow("Please enter at least one link.");
                errorWin.Owner = this;
                errorWin.ShowDialog();
                return;
            }

            Links = Regex.Matches(text, @"(?:https?://)?(?:www\.)?gofile\.io/d/[a-zA-Z0-9\-]+", RegexOptions.IgnoreCase)
                         .Cast<Match>()
                         .Select(m => m.Value)
                         .Distinct()
                         .ToList();

            if (!Links.Any())
            {
                var errorWin = new GlobalErrorWindow("No valid GoFile links found in the text.");
                errorWin.Owner = this;
                errorWin.ShowDialog();
                return;
            }

            DialogResult = true;
            Close();
        }

        private void Window_Deactivated(object sender, System.EventArgs e)
        {
            try { Close(); } catch { }
        }
    }
}
