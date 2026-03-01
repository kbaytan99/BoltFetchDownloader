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
using BoltFetch.Services;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace BoltFetch
{
    public partial class MainWindow : Window
    {
        private readonly GoFileService _goFileService = new GoFileService();
        private readonly DownloadManager _downloadManager = new DownloadManager(3);
        private UserSettings _settings;
        private readonly ClipboardMonitor _clipboardMonitor = new ClipboardMonitor();
        private readonly NotificationService _notificationService = new NotificationService();
        private readonly DownloadOrchestrator _orchestrator;
        
        public ObservableCollection<FileDisplayItem> FileItems { get; } = new ObservableCollection<FileDisplayItem>();

        private Forms.NotifyIcon? _notifyIcon;
        private string _lastCapturedLink = string.Empty;
        
        // Speed Graph fields
        private readonly List<double> _speedHistory = new List<double>();
        private const int MaxHistoryPoints = 60;
        private DateTime _lastGraphUpdate = DateTime.MinValue;

        public MainWindow()
        {
            InitializeComponent();
            _settings = SettingsService.Load();
            _orchestrator = new DownloadOrchestrator(_downloadManager, _settings);
            
            // Handle graph redraw on resize
            SpeedGraphCanvas.SizeChanged += (s, e) => DrawSpeedGraph();
            
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

            // Setup Clipboard Monitor from Service
            _clipboardMonitor.LinksDetected += (links) => Dispatcher.Invoke(() => HandleDetectedLinks(links));
            _clipboardMonitor.Start();

            // Setup NotifyIcon
            SetupTrayIcon();
        }

        // --- Custom Title Bar Controls ---
        private void MinimizeBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void MaximizeBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private async void HandleDetectedLinks(string[] links)
        {
            await FetchFilesBatch(links.ToList());
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

        private async void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AddLinkWindow { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Links.Any())
            {
                await FetchFilesBatch(dialog.Links);
            }
        }

        private async Task FetchFilesBatch(List<string> urls)
        {
            Dispatcher.Invoke(() => AddButton.IsEnabled = false);

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
                    _notificationService.ShowPopup($"{totalAdded} Files Found! 🚀", $"{totalAdded} files ({FormatSizeGB(totalSize)}) added to the list.");
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => _notificationService.ShowMessage($"Error fetching folder content: {ex.Message}"));
            }
            finally
            {
                Dispatcher.Invoke(() => {
                    AddButton.IsEnabled = true;
                    UpdateSummary();
                });
            }
        }


        private void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = FilesDataGrid.SelectedItems.Cast<FileDisplayItem>().ToList();
            if (!selectedItems.Any())
            {
                _notificationService.ShowMessage("Please select files to download.");
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

            _ = _orchestrator.ProcessQueueAsync(FileItems);
            
            DownloadButton.IsEnabled = true;
        }

        // StartDownload removed as it's now in DownloadOrchestrator

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
                item.Status = "Queued";
                _ = _orchestrator.ProcessQueueAsync(FileItems);
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
                    _notificationService.ShowMessage("File not found on disk.");
                }
            }
        }

        private void RemoveItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is FileDisplayItem item)
            {
                var result = System.Windows.MessageBox.Show($"Are you sure you want to remove '{item.Name}' from the list?\n\n(This will NOT delete the file from your PC)", 
                    "Confirm Removal", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
                
                if (result == System.Windows.MessageBoxResult.Yes)
                {
                    FileItems.Remove(item);
                    UpdateSummary();
                }
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
                    DeleteFilesForDisplayItem(item);

                    // 3. Remove from UI list
                    FileItems.Remove(item);
                    UpdateSummary();
                }
            }
        }

        private void DeleteFilesForDisplayItem(FileDisplayItem item)
        {
            try
            {
                var filePath = Path.Combine(_settings.DownloadPath, item.Name);
                if (File.Exists(filePath)) File.Delete(filePath);
                
                var downloadingPath = filePath + ".downloading";
                if (File.Exists(downloadingPath)) File.Delete(downloadingPath);
                
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
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            FilesDataGrid.SelectAll();
        }

        private void RemoveAll_Click(object sender, RoutedEventArgs e)
        {
            var itemsToRemove = FilesDataGrid.SelectedItems.Count > 0 
                ? FilesDataGrid.SelectedItems.Cast<FileDisplayItem>().ToList() 
                : FileItems.ToList(); // If none selected, act on all? User wants "Select All" -> they will probably select. Or "Remove All" means ALL.
            
            // Let's make "Remove All" act on all selected items if there are any, otherwise ALL items.
            // Actually, naming is "Remove All", meaning it asks to remove everything.
            string prompt = FilesDataGrid.SelectedItems.Count > 0 
                ? $"Are you sure you want to remove the selected {FilesDataGrid.SelectedItems.Count} items from the list?"
                : $"Are you sure you want to remove ALL {FileItems.Count} items from the list?";

            var result = System.Windows.MessageBox.Show(prompt + "\n\n(This will NOT delete files from your PC)", 
                "Confirm Mass Removal", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);

            if (result == System.Windows.MessageBoxResult.Yes)
            {
                var targetItems = FilesDataGrid.SelectedItems.Count > 0 
                    ? FilesDataGrid.SelectedItems.Cast<FileDisplayItem>().ToList() 
                    : FileItems.ToList();

                foreach (var item in targetItems)
                {
                    FileItems.Remove(item);
                }
                UpdateSummary();
            }
        }

        private void DeleteAll_Click(object sender, RoutedEventArgs e)
        {
            var itemsToDelete = FilesDataGrid.SelectedItems.Count > 0 
                ? FilesDataGrid.SelectedItems.Cast<FileDisplayItem>().ToList() 
                : FileItems.ToList();
            
            string prompt = FilesDataGrid.SelectedItems.Count > 0 
                ? $"Are you sure you want to PERMANENTLY delete the selected {FilesDataGrid.SelectedItems.Count} items from disk AND list?"
                : $"Are you sure you want to PERMANENTLY delete ALL {FileItems.Count} items from disk AND list?";

            var result = System.Windows.MessageBox.Show(prompt + "\n\nThis cannot be undone.", 
                "Confirm Mass Deletion", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);

            if (result == System.Windows.MessageBoxResult.Yes)
            {
                var targetItems = FilesDataGrid.SelectedItems.Count > 0 
                    ? FilesDataGrid.SelectedItems.Cast<FileDisplayItem>().ToList() 
                    : FileItems.ToList();

                foreach (var item in targetItems)
                {
                    _downloadManager.CancelDownload(item.Source.Id);
                    DeleteFilesForDisplayItem(item);
                    FileItems.Remove(item);
                }
                UpdateSummary();
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
                _ = _orchestrator.ProcessQueueAsync(FileItems); // Check for next items in queue
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
                    item.ProgressText = error;
                    item.ProgressValue = 0;
                    item.ETAText = "Failed";
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
                    item.ProgressText = "User Cancelled";
                    item.ProgressValue = 0;
                    item.ETAText = "Cancelled";
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
            TotalSpeedText.Text = FormatSpeed(totalSpeedBytes) + "/s";

            // Update Graph up to twice per second
            if ((DateTime.Now - _lastGraphUpdate).TotalSeconds >= 0.5)
            {
                _lastGraphUpdate = DateTime.Now;
                _speedHistory.Add(totalSpeedBytes);
                if (_speedHistory.Count > MaxHistoryPoints)
                {
                    _speedHistory.RemoveAt(0);
                }
                DrawSpeedGraph();
            }
        }

        private void DrawSpeedGraph()
        {
            if (_speedHistory.Count < 2) return;

            double width = SpeedGraphCanvas.ActualWidth;
            double height = SpeedGraphCanvas.ActualHeight;

            if (width <= 0 || height <= 0) return;

            double maxSpeed = _speedHistory.Max();
            if (maxSpeed == 0) maxSpeed = 1; // Prevent division by zero

            var linePoints = new System.Windows.Media.PointCollection();
            var fillPoints = new System.Windows.Media.PointCollection();

            fillPoints.Add(new System.Windows.Point(0, height)); // Bottom-left corner (starting fill)

            double stepX = width / (MaxHistoryPoints - 1);

            for (int i = 0; i < _speedHistory.Count; i++)
            {
                int age = _speedHistory.Count - 1 - i;
                double x = width - (age * stepX);
                double y = height - ((_speedHistory[i] / maxSpeed) * height);
                // Clamp Y
                y = Math.Max(0, Math.Min(height, y));

                var pt = new System.Windows.Point(x, y);
                linePoints.Add(pt);
                fillPoints.Add(pt);
            }

            fillPoints.Add(new System.Windows.Point(width, height)); // Bottom-right corner (ending fill)

            SpeedGraphLine.Points = linePoints;
            SpeedGraphFill.Points = fillPoints;
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

        public DownloadProgress? SourceProgress { get; set; }

        public FileDisplayItem(GoFileItem source) => Source = source;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}