using System;
using System.Windows;
using BoltFetch.Models;
using Microsoft.Win32;

namespace BoltFetch
{
    public partial class SettingsWindow : Window
    {
        public UserSettings Settings { get; private set; }

        public SettingsWindow(UserSettings currentSettings)
        {
            InitializeComponent();
            Settings = currentSettings;
            PathTextBox.Text = Settings.DownloadPath;
            SpeedLimitTextBox.Text = Settings.SpeedLimitKB.ToString();
            SegmentsTextBox.Text = Settings.SegmentsPerFile.ToString();
            ParallelTextBox.Text = Settings.MaxParallelDownloads.ToString();
            
            // Set initial language selection visually
            foreach (System.Windows.Controls.ComboBoxItem item in LanguageComboBox.Items)
            {
                if (item.Tag?.ToString() == Settings.Language)
                {
                    item.IsSelected = true;
                    break;
                }
            }
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog();
            dialog.InitialDirectory = Settings.DownloadPath;
            if (dialog.ShowDialog() == true)
            {
                PathTextBox.Text = dialog.FolderName;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(SpeedLimitTextBox.Text, out int speed) && 
                int.TryParse(SegmentsTextBox.Text, out int segments) &&
                int.TryParse(ParallelTextBox.Text, out int parallel))
            {
                Settings.DownloadPath = PathTextBox.Text;
                Settings.SpeedLimitKB = speed;
                Settings.SegmentsPerFile = segments;
                Settings.MaxParallelDownloads = Math.Max(1, parallel); // Ensure at least 1
                
                if (LanguageComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem)
                {
                    Settings.Language = selectedItem.Tag?.ToString() ?? "en";
                }
                
                DialogResult = true;
                Close();
            }
            else
            {
                var errorWin = new GlobalErrorWindow("Please enter valid numeric values.");
                errorWin.Owner = this;
                errorWin.ShowDialog();
            }
        }

        private void ReportBug_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/kbaytan99/BoltFetch-Downloader/issues/new",
                UseShellExecute = true
            });
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Window_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                this.DragMove();
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // Prevent accidental closures during drag operation
        }
    }
}
