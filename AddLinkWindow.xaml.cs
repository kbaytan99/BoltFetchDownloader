using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;

namespace BoltFetch
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

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var text = LinksTextBox.Text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                System.Windows.MessageBox.Show("Please enter at least one link.", "Notice", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            Links = Regex.Matches(text, @"https://gofile\.io/d/[a-zA-Z0-9]+")
                         .Cast<Match>()
                         .Select(m => m.Value)
                         .Distinct()
                         .ToList();

            if (!Links.Any())
            {
                System.Windows.MessageBox.Show("No valid GoFile links found in the text.", "Notice", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
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
