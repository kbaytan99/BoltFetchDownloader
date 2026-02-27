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
                DialogResult = true;
                Close();
            }
            else
            {
                System.Windows.MessageBox.Show("Please enter valid numeric values.");
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
