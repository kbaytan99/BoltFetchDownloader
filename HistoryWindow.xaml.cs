using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using BoltFetch.Services;
using System.Text.Json;

namespace BoltFetch
{
    public partial class HistoryWindow : Window
    {
        public ObservableCollection<HistoryDisplayItem> HistoryItems { get; set; } = new();

        public HistoryWindow()
        {
            InitializeComponent();
            HistoryDataGrid.ItemsSource = HistoryItems;
            LoadHistory();
        }

        private void LoadHistory()
        {
            var profilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smart_profile.json");
            if (!File.Exists(profilePath)) return;

            try
            {
                var bytes = File.ReadAllBytes(profilePath);
                var profile = JsonSerializer.Deserialize<SmartProfile>(bytes);
                if (profile?.History == null) return;

                var items = profile.History
                    .OrderByDescending(r => r.Timestamp)
                    .Select(r => new HistoryDisplayItem(r))
                    .ToList();

                foreach (var item in items)
                {
                    HistoryItems.Add(item);
                }

                double totalGB = profile.History.Sum(r => r.FileSize) / (1024.0 * 1024 * 1024);
                TotalDownloadedText.Text = $"{totalGB:F2} GB";
            }
            catch (Exception ex)
            {
                var errorWin = new GlobalErrorWindow("Error loading history: " + ex.Message);
                errorWin.Owner = this;
                errorWin.ShowDialog();
            }
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

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            try { Close(); } catch { }
        }
    }

    public class HistoryDisplayItem
    {
        public DateTime Timestamp { get; set; }
        public string FileName { get; set; }
        public string SizeDisplay { get; set; }
        public string Category { get; set; }

        public HistoryDisplayItem(DownloadRecord record)
        {
            Timestamp = record.Timestamp;
            FileName = record.FileName;
            Category = record.Category;
            SizeDisplay = FormatSize(record.FileSize);
        }

        private string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unitIndex = 0;
            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }
            return $"{size:F2} {units[unitIndex]}";
        }
    }
}
