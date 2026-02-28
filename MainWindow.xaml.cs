using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BoltFetch.Models;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace BoltFetch
{
    public partial class MainWindow : Window
    {
        private readonly GoFileService _goFileService = new GoFileService();
        private readonly DownloadManager _downloadManager = new DownloadManager(3);
        private UserSettings _settings = new UserSettings();
        public ObservableCollection<FileDisplayItem> FileItems { get; } = new ObservableCollection<FileDisplayItem>();

        private Forms.NotifyIcon _notifyIcon;
        private string _lastCapturedLink = string.Empty;

        public MainWindow()
        {
            InitializeComponent();
            _settings = SettingsService.Load();
            _downloadManager.SegmentsPerFile = _settings.SegmentsPerFile;
            _downloadManager.SpeedLimitKB = _settings.SpeedLimitKB;
            PathLabel.Text = _settings.DownloadPath;
            ParallelLimitText.Text = _settings.MaxParallelDownloads.ToString();
            FilesDataGrid.ItemsSource = FileItems;

            _downloadManager.ProgressChanged += OnDownloadProgressChanged;
            _downloadManager.DownloadCompleted += OnDownloadCompleted;
            _downloadManager.DownloadFailed += OnDownloadFailed;
            _downloadManager.DownloadCancelled += OnDownloadCancelled;

            FileItems.CollectionChanged += (s, e) => UpdateSummary();

            // Setup Clipboard Monitor
            var clipboardTimer = new DispatcherTimer();
            clipboardTimer.Interval = TimeSpan.FromSeconds(1.5);
            clipboardTimer.Tick += ClipboardTimer_Tick;
            clipboardTimer.Start();

            // Setup NotifyIcon
            SetupTrayIcon();
        }

        private void SetupTrayIcon()
        {
            try
            {
                _notifyIcon = new Forms.NotifyIcon();
                // Try to get a high quality icon from the resource or exe
                _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location);
                _notifyIcon.Visible = true;
                _notifyIcon.Text = "BoltFetch Downloader";
                _notifyIcon.DoubleClick += (s, e) => RestoreFromTray();

                var contextMenu = new Forms.ContextMenuStrip();
                contextMenu.Items.Add("Restore", null, (s, e) => RestoreFromTray());
                contextMenu.Items.Add("-");
                contextMenu.Items.Add("Exit", null, (s, e) => System.Windows.Application.Current.Shutdown());
                _notifyIcon.ContextMenuStrip = contextMenu;
            }
            catch { }
        }

        private void RestoreFromTray()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                this.Hide();
                if (_notifyIcon != null)
                {
                    _notifyIcon.ShowBalloonTip(2000, "BoltFetch", "Uygulama arka planda çalışmaya devam ediyor.", Forms.ToolTipIcon.Info);
                }
            }
            base.OnStateChanged(e);
        }

        private void ClipboardTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    string text = System.Windows.Clipboard.GetText().Trim();
                    if (string.IsNullOrEmpty(text) || text == _lastCapturedLink) return;

                    // Support multiple links separated by spaces, newlines, etc.
                    var links = Regex.Matches(text, @"https://gofile\.io/d/[a-zA-Z0-9]+")
                                     .Cast<Match>()
                                     .Select(m => m.Value)
                                     .Distinct()
                                     .ToList();

                    if (links.Any())
                    {
                        _lastCapturedLink = text;
                        FetchFilesBatch(links);

                        // Visual feedback
                        LinkBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(58, 190, 249));
                        System.Threading.Tasks.Task.Delay(1000).ContinueWith(_ => Dispatcher.Invoke(() => LinkBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85))));
                    }
                }
            }
            catch { }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow(_settings);
            settingsWindow.Owner = this;
            if (settingsWindow.ShowDialog() == true)
            {
                _settings = settingsWindow.Settings;
                SettingsService.Save(_settings);
                PathLabel.Text = _settings.DownloadPath;
                _downloadManager.SpeedLimitKB = _settings.SpeedLimitKB;
                _downloadManager.SegmentsPerFile = _settings.SegmentsPerFile;
                _downloadManager.UpdateParallelLimit(_settings.MaxParallelDownloads);
                ParallelLimitText.Text = _settings.MaxParallelDownloads.ToString();
            }
        }

        private async void FetchButton_Click(object sender, RoutedEventArgs e)
        {
            var text = LinkTextBox.Text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                System.Windows.MessageBox.Show("Please enter valid GoFile link(s).");
                return;
            }

            var links = Regex.Matches(text, @"https://gofile\.io/d/[a-zA-Z0-9]+")
                             .Cast<Match>()
                             .Select(m => m.Value)
                             .Distinct()
                             .ToList();

            if (links.Any())
            {
                await FetchFilesBatch(links);
                LinkTextBox.Text = string.Empty; // Clear after successful add
            }
            else
            {
                System.Windows.MessageBox.Show("Please enter valid GoFile link(s).");
            }
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            // Just trigger fetch as it handles links in the text box
            FetchButton_Click(sender, e);
        }

        private async Task FetchFilesBatch(List<string> urls)
        {
            Dispatcher.Invoke(() => FetchButton.IsEnabled = false);

            int totalAdded = 0;
            long totalSize = 0;

            try
            {
                foreach (var url in urls)
                {
                    var folderCode = url.Split('/').LastOrDefault();
                    if (string.IsNullOrEmpty(folderCode)) continue;

                    var items = await _goFileService.GetFolderContents(folderCode);
                    Dispatcher.Invoke(() => {
                        foreach (var goItem in items)
                        {
                            if (FileItems.Any(i => i.Source.Id == goItem.Id)) continue; // Avoid duplicates

                            var displayItem = new FileDisplayItem(goItem);
                            var tempProgress = new DownloadProgress { FileName = goItem.Name, TotalBytes = goItem.Size };
                            _downloadManager.UpdateProgressFromExistingParts(goItem, _settings.DownloadPath, tempProgress);
                            
                            displayItem.ProgressValue = tempProgress.ProgressPercentage;
                            displayItem.ProgressText = tempProgress.ProgressText;
                            
                            if (displayItem.ProgressValue >= 100)
                            {
                                displayItem.Status = "Completed";
                                displayItem.ProgressValue = 100;
                            }
                            else if (displayItem.ProgressValue > 0)
                            {
                                displayItem.Status = "Stopped";
                            }

                            FileItems.Add(displayItem);
                            totalAdded++;
                            totalSize += goItem.Size;
                        }
                    });
                }

                if (totalAdded > 0)
                {
                    ShowNotification($"{totalAdded} Files Found! 🚀", $"{totalAdded} files ({FormatSizeGB(totalSize)}) added to the list.");
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => System.Windows.MessageBox.Show($"Error fetching folder content: {ex.Message}"));
            }
            finally
            {
                Dispatcher.Invoke(() => {
                    FetchButton.IsEnabled = true;
                    UpdateSummary();
                });
            }
        }

        private void ShowNotification(string title, string message)
        {
            Dispatcher.Invoke(() => {
                var notify = new NotificationWindow(title, message);
                notify.Show();
            });
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = FilesDataGrid.SelectedItems.Cast<FileDisplayItem>().ToList();
            if (!selectedItems.Any())
            {
                System.Windows.MessageBox.Show("Please select files to download.");
                return;
            }

            DownloadButton.IsEnabled = false;
            var path = _settings.DownloadPath;

            // Mark selected items as ready for the queue
            foreach (var item in selectedItems)
            {
                if (item.Status == "Pending" || item.Status == "Stopped" || item.Status == "Cancelled" || item.Status == "Error")
                {
                    item.Status = "Queued";
                }
            }

            _ = ProcessQueue(); // Start the background processor
            
            DownloadButton.IsEnabled = true;
        }

        private async Task ProcessQueue()
        {
            // Simple loop to fill slots
            while (true)
            {
                var queuedItems = FileItems.Where(i => i.Status == "Queued").ToList();
                if (!queuedItems.Any()) break;

                // Check how many we can start
                int activeCount = FileItems.Count(i => i.Status == "Downloading...");
                int slotsAvailable = _settings.MaxParallelDownloads - activeCount;

                if (slotsAvailable <= 0)
                {
                    await Task.Delay(1000);
                    continue;
                }

                foreach (var item in queuedItems.Take(slotsAvailable))
                {
                    _ = StartDownload(item, _settings.DownloadPath);
                }

                await Task.Delay(1000);
            }
        }

        private async Task StartDownload(FileDisplayItem item, string path)
        {
            if (item.Status == "Downloading..." || item.Status == "Completed") return;
            
            item.Status = "Downloading...";
            await _downloadManager.DownloadFileAsync(item.Source, path);
        }

        private void StopItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is FileDisplayItem item)
            {
                _downloadManager.CancelDownload(item.Source.Id);
            }
        }

        private void RetryItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is FileDisplayItem item)
            {
                StartDownload(item, _settings.DownloadPath);
            }
        }

        private void LocateItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is FileDisplayItem item)
            {
                var filePath = Path.Combine(_settings.DownloadPath, item.Name);
                if (File.Exists(filePath))
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
                }
                else
                {
                    System.Windows.MessageBox.Show("File not found on disk.");
                }
            }
        }

        private void RemoveItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is FileDisplayItem item)
            {
                // Only remove from list, keep file on disk
                FileItems.Remove(item);
                UpdateSummary();
            }
        }

        private void DeleteItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is FileDisplayItem item)
            {
                var result = System.Windows.MessageBox.Show($"Are you sure you want to PERMANENTLY delete '{item.Name}' from disk AND list?\n\nThis cannot be undone.", 
                    "Confirm Permanent Deletion", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
                
                if (result == System.Windows.MessageBoxResult.Yes)
                {
                    // 1. Cancel if active
                    _downloadManager.CancelDownload(item.Source.Id);
                    
                    // 2. Delete files from disk
                    try
                    {
                        var filePath = Path.Combine(_settings.DownloadPath, item.Name);
                        if (File.Exists(filePath)) File.Delete(filePath);
                        
                        // Clean up fragments (.part files)
                        for (int i = 1; i <= 64; i++) // Scan a bit higher just in case
                        {
                            var partPath = filePath + ".part" + i;
                            if (File.Exists(partPath)) File.Delete(partPath);
                        }
                    }
                    catch (Exception ex)
                    {
                    System.Windows.MessageBox.Show($"Error deleting files from disk: {ex.Message}");
                    }

                    // 3. Remove from UI list
                    FileItems.Remove(item);
                    UpdateSummary();
                }
            }
        }

        private void OnDownloadProgressChanged(string itemId, DownloadProgress progress)
        {
            Dispatcher.Invoke(() =>
            {
                var item = FileItems.FirstOrDefault(i => i.Source.Id == itemId);
                if (item != null)
                {
                    item.ProgressValue = progress.ProgressPercentage;
                    item.ProgressText = progress.ProgressText;
                    item.SpeedText = progress.SpeedText;
                    item.ETAText = progress.ETAText;
                    item.SourceProgress = progress; // Save reference for summary
                }
                UpdateSummary();
            });
        }

        private void OnDownloadCompleted(string itemId, string path)
        {
            Dispatcher.Invoke(() =>
            {
                var item = FileItems.FirstOrDefault(i => i.Source.Id == itemId);
                if (item != null)
                {
                    item.Status = "Completed";
                    item.ProgressValue = 100;
                    item.ProgressText = "100.0%";
                    item.SpeedText = "-";
                    item.ETAText = "Done";
                }
                UpdateSummary();
                _ = ProcessQueue(); // Check for next items in queue
            });
        }

        private void OnDownloadFailed(string itemId, string error)
        {
            Dispatcher.Invoke(() =>
            {
                var item = FileItems.FirstOrDefault(i => i.Source.Id == itemId);
                if (item != null)
                {
                    item.Status = "Error";
                    item.SpeedText = "0 KB/s";
                }
                UpdateSummary();
            });
        }

        private void OnDownloadCancelled(string itemId)
        {
            Dispatcher.Invoke(() =>
            {
                var item = FileItems.FirstOrDefault(i => i.Source.Id == itemId);
                if (item != null)
                {
                    item.Status = "Cancelled";
                    item.SpeedText = "0 KB/s";
                }
                UpdateSummary();
            });
        }

        private void UpdateSummary()
        {
            long totalBytes = FileItems.Sum(i => i.Source.Size);
            
            int queued = FileItems.Count(i => i.Status == "Pending");
            int active = FileItems.Count(i => i.Status == "Downloading...");
            int finished = FileItems.Count(i => i.Status == "Completed");

            TotalSizeText.Text = FormatSizeGB(totalBytes);
            QueuedCountText.Text = (queued + FileItems.Count(i => i.Status == "Queued")).ToString();
            ActiveCountText.Text = active.ToString();
            FinishedCountText.Text = finished.ToString();
            
            // Calculate Total Speed
            long totalSpeedBytes = FileItems.Where(i => i.Status == "Downloading...").Sum(i => i.SourceProgress?.SpeedBytesPerSecond ?? 0);
            TotalSpeedText.Text = FormatSpeed(totalSpeedBytes);
        }

        private string FormatSpeed(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unitIndex = 0;
            while (size >= 1024 && unitIndex < units.Length - 1) { size /= 1024; unitIndex++; }
            return $"{size:F2} {units[unitIndex]}/s";
        }

        private string FormatSizeGB(long bytes)
        {
            double gb = (double)bytes / (1024 * 1024 * 1024);
            return $"{gb:F2} GB";
        }
    }

    public class FileDisplayItem : INotifyPropertyChanged
    {
        public GoFileItem Source { get; }
        public string Name => Source.Name;
        public string SizeFormatted => Source.SizeFormatted;

        private double _progressValue;
        public double ProgressValue { get => _progressValue; set { _progressValue = value; OnPropertyChanged(); } }

        private string _progressText = "0%";
        public string ProgressText { get => _progressText; set { _progressText = value; OnPropertyChanged(); } }

        private string _speedText = "-";
        public string SpeedText { get => _speedText; set { _speedText = value; OnPropertyChanged(); } }

        private string _etaText = "--:--";
        public string ETAText { get => _etaText; set { _etaText = value; OnPropertyChanged(); } }

        private string _status = "Pending";
        public string Status 
        { 
            get => _status; 
            set 
            { 
                _status = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(IsCompleted)); 
                OnPropertyChanged(nameof(IsNotCompleted));
            } 
        }

        public bool IsCompleted => Status == "Completed";
        public bool IsNotCompleted => !IsCompleted;

        public DownloadProgress SourceProgress { get; set; }

        public FileDisplayItem(GoFileItem source) => Source = source;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}