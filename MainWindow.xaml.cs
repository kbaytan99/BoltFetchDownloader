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
        private readonly DownloadManager _downloadManager;
        private UserSettings _settings;
        private readonly ClipboardMonitor _clipboardMonitor = new ClipboardMonitor();
        private readonly NotificationService _notificationService = new NotificationService();
        private readonly DownloadOrchestrator _orchestrator;
        
        public ObservableCollection<FileDisplayItem> FileItems { get; } = new ObservableCollection<FileDisplayItem>();

        private Forms.NotifyIcon? _notifyIcon;
        
        // Speed Graph fields
        private readonly List<double> _speedHistory = new List<double>();
        private DispatcherTimer _memoryTimer;
        private int _lastActiveCount = 0;
        private const int MaxHistoryPoints = 60;
        private DateTime _lastGraphUpdate = DateTime.MinValue;
        private System.Windows.Point _dragStartPoint;
        private FileDisplayItem? _draggedItem;

        public MainWindow()
        {
            InitializeComponent();
            _settings = SettingsService.Load();
            _downloadManager = new DownloadManager(_settings.MaxParallelDownloads);
            _orchestrator = new DownloadOrchestrator(_downloadManager, _settings);
            
            _memoryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _memoryTimer.Tick += (s, e) => UpdateMemoryUsage();
            _memoryTimer.Start();
            
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

            // Load saved language
            ApplyLanguage(_settings.Language);

            // Restore column widths and saved download queue
            RestoreColumnWidths();
            RestoreQueue();

            // Detect internet link speed
            try
            {
                var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                        && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    .OrderByDescending(n => n.Speed)
                    .FirstOrDefault();
                if (nic != null)
                {
                    double speedMbps = nic.Speed / 1_000_000.0;
                    InternetSpeedText.Text = speedMbps >= 1000
                        ? $"↓ {speedMbps / 1000:F1} Gbps"
                        : $"↓ {speedMbps:F0} Mbps";
                }
            }
            catch { InternetSpeedText.Text = "-"; }
        }

        #region Title Bar Controls
        private void MinimizeBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void MaximizeBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
        #endregion

        private async void HandleDetectedLinks(string[] links)
        {
            await FetchFilesBatch(links.ToList());
        }

        #region System Tray & About logic
        private void SetupTrayIcon()
        {
            try
            {
                _notifyIcon = new Forms.NotifyIcon();
                
                System.Drawing.Icon appIcon = System.Drawing.SystemIcons.Information;
                try 
                {
                    var streamInfo = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Resources/logo.png"));
                    if (streamInfo != null)
                    {
                        appIcon = GetTrayIcon(streamInfo.Stream);
                        // Windows Bar (Taskbar) iconunu da ayni kirpilmis ve buyutulmus logoyla degistiriyoruz
                        var wpfIcon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                            appIcon.Handle,
                            System.Windows.Int32Rect.Empty,
                            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                        this.Icon = wpfIcon;
                    }
                }
                catch { }

                _notifyIcon.Icon = appIcon;
                
                _notifyIcon.Visible = true;
                _notifyIcon.Text = "BoltFetch Downloader";
                _notifyIcon.DoubleClick += (s, e) => RestoreFromTray();

                var contextMenu = new Forms.ContextMenuStrip();
                contextMenu.Items.Add("Settings", null, (s, e) => Dispatcher.Invoke(() => SettingsButton_Click(null!, null!)));
                contextMenu.Items.Add("About", null, (s, e) => Dispatcher.Invoke(() => ShowAboutWindow()));
                contextMenu.Items.Add("-");
                contextMenu.Items.Add("Restore", null, (s, e) => RestoreFromTray());
                contextMenu.Items.Add("-");
                contextMenu.Items.Add("Exit", null, (s, e) => System.Windows.Application.Current.Shutdown());
                _notifyIcon.ContextMenuStrip = contextMenu;
            }
            catch (Exception ex) 
            {
                System.Windows.MessageBox.Show("Tray icon kurulum hatası: " + ex.Message, "Hata", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void RestoreFromTray()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        private void ShowAboutWindow()
        {
            var aboutWindow = new AboutWindow();
            aboutWindow.Owner = this;
            aboutWindow.ShowDialog();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                // Dispatcher.BeginInvoke kullanarak Hide işlemini asenkron yapıyoruz ki UI thread kilitlenmesin
                this.Dispatcher.BeginInvoke(new Action(() => 
                {
                    this.Hide();
                }));
            }
            base.OnStateChanged(e);
        }

        #endregion

        #region General UI Methods
        private System.Drawing.Icon GetTrayIcon(System.IO.Stream stream)
        {
            using (var bmp = new System.Drawing.Bitmap(stream))
            {
                // Buyukluk-kucukluk sorununu cozmek icin saydam bosluklari otomatik kirpiyoruz
                int left = bmp.Width, top = bmp.Height, right = 0, bottom = 0;
                for (int y = 0; y < bmp.Height; y += 2) // Hizli tarama icin 2'ser atliyoruz
                {
                    for (int x = 0; x < bmp.Width; x += 2)
                    {
                        if (bmp.GetPixel(x, y).A > 10)
                        {
                            if (x < left) left = x;
                            if (x > right) right = x;
                            if (y < top) top = y;
                            if (y > bottom) bottom = y;
                        }
                    }
                }

                int w = right - left + 1;
                int h = bottom - top + 1;
                if (w <= 0 || h <= 0) { w = bmp.Width; h = bmp.Height; left = 0; top = 0; }

                // Ikona en iyi yerlesmesi icin resmin tam sığacağı bir kare olustur
                int size = Math.Max(w, h);
                var squareBmp = new System.Drawing.Bitmap(size, size);
                using (var g = System.Drawing.Graphics.FromImage(squareBmp))
                {
                    int drawX = (size - w) / 2;
                    int drawY = (size - h) / 2;
                    g.DrawImage(bmp, new System.Drawing.Rectangle(drawX, drawY, w, h), new System.Drawing.Rectangle(left, top, w, h), System.Drawing.GraphicsUnit.Pixel);
                }

                // Tepsi icin cok kaliteli (128x128) pürüzsüz scale yapiyoruz, GetHicon daha sonra boyutlandirabilir ama elimizde kaliteli data olur
                var finalBmp = new System.Drawing.Bitmap(128, 128);
                using (var g = System.Drawing.Graphics.FromImage(finalBmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(squareBmp, 0, 0, 128, 128);
                }

                return System.Drawing.Icon.FromHandle(finalBmp.GetHicon());
            }
        }


        private void HistoryButton_Click(object sender, RoutedEventArgs e)
        {
            var win = new HistoryWindow();
            win.Owner = this;
            win.ShowDialog();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow(_settings);
            settingsWindow.Owner = this;
            if (settingsWindow.ShowDialog() == true)
            {
                var oldLang = _settings.Language;
                _settings = settingsWindow.Settings;
                SettingsService.Save(_settings);
                PathLabel.Text = _settings.DownloadPath;
                _downloadManager.SpeedLimitKB = _settings.SpeedLimitKB;
                _downloadManager.SegmentsPerFile = _settings.SegmentsPerFile;
                _downloadManager.UpdateParallelLimit(_settings.MaxParallelDownloads);
                _orchestrator.UpdateSettings(_settings);
                ParallelLimitText.Text = _settings.MaxParallelDownloads.ToString();

                if (oldLang != _settings.Language)
                {
                    ApplyLanguage(_settings.Language);
                }
            }
        }

        private void ApplyLanguage(string langCode)
        {
            var dict = new ResourceDictionary();
            dict.Source = new Uri($"Locales/{langCode}.xaml", UriKind.Relative);

            // Remove old locale dictionaries and add the new one
            var toRemove = System.Windows.Application.Current.Resources.MergedDictionaries
                .Where(d => d.Source != null && d.Source.OriginalString.StartsWith("Locales/")).ToList();
            foreach (var d in toRemove) System.Windows.Application.Current.Resources.MergedDictionaries.Remove(d);
            System.Windows.Application.Current.Resources.MergedDictionaries.Add(dict);

            // Force DataGrid column headers to refresh (they don't auto-update DynamicResource)
            string[] headerKeys = { "Loc_ColName", "Loc_ColSize", "Loc_ColProgress", "Loc_ColSpeed", "Loc_ColETA", "Loc_ColStatus", "Loc_ColControls" };
            for (int i = 0; i < FilesDataGrid.Columns.Count && i < headerKeys.Length; i++)
            {
                FilesDataGrid.Columns[i].Header = FindResource(headerKeys[i]);
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
        #endregion

        #region Download Queue Initialization
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
        #endregion

        #region User Actions & UI Commands
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
                if (ConfirmDialog.Show(this, "Remove Item", $"Remove '{item.Name}' from the list?\n\nThe file will NOT be deleted from your PC.", "Remove"))
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
                if (ConfirmDialog.Show(this, "Permanent Deletion", $"PERMANENTLY delete '{item.Name}' from disk AND list?\n\nThis cannot be undone.", "Delete", isDanger: true))
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
                
                var statePath = filePath + ".downloading.state";
                if (File.Exists(statePath)) File.Delete(statePath);
                
                var oldStatePath = filePath + ".state";
                if (File.Exists(oldStatePath)) File.Delete(oldStatePath);
                
                // Clean up old fragments (.part files)
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
            var targetItems = FilesDataGrid.SelectedItems.Count > 0 
                ? FilesDataGrid.SelectedItems.Cast<FileDisplayItem>().ToList() 
                : FileItems.ToList();
            
            string prompt = targetItems.Count == FileItems.Count 
                ? $"Are you sure you want to remove ALL {FileItems.Count} items from the list?"
                : $"Are you sure you want to remove the selected {targetItems.Count} items from the list?";

            if (ConfirmDialog.Show(this, "Remove Items", prompt + "\n\nFiles will NOT be deleted from your PC.", "Remove"))
            {
                foreach (var item in targetItems)
                {
                    FileItems.Remove(item);
                }
                UpdateSummary();
            }
        }

        private void DeleteAll_Click(object sender, RoutedEventArgs e)
        {
            var targetItems = FilesDataGrid.SelectedItems.Count > 0 
                ? FilesDataGrid.SelectedItems.Cast<FileDisplayItem>().ToList() 
                : FileItems.ToList();
            
            string prompt = targetItems.Count == FileItems.Count 
                ? $"Are you sure you want to PERMANENTLY delete ALL {FileItems.Count} items from disk AND list?"
                : $"Are you sure you want to PERMANENTLY delete the selected {targetItems.Count} items from disk AND list?";

            if (ConfirmDialog.Show(this, "Permanent Deletion", prompt + "\n\nThis cannot be undone.", "Delete All", isDanger: true))
            {
                foreach (var item in targetItems)
                {
                    _downloadManager.CancelDownload(item.Source.Id);
                    DeleteFilesForDisplayItem(item);
                    FileItems.Remove(item);
                }
                UpdateSummary();
            }
        }
        #endregion

        #region Drag & Drop Reordering
        private void FilesDataGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
        }

        private void FilesDataGrid_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                System.Windows.Point currentPoint = e.GetPosition(null);
                if (Math.Abs(currentPoint.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(currentPoint.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    _draggedItem = GetDataGridItemUnderMouse(e.GetPosition(FilesDataGrid));
                    if (_draggedItem != null)
                    {
                        DragDrop.DoDragDrop(FilesDataGrid, _draggedItem, System.Windows.DragDropEffects.Move);
                    }
                }
            }
        }

        private void FilesDataGrid_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (_draggedItem == null) return;

            var targetItem = GetDataGridItemUnderMouse(e.GetPosition(FilesDataGrid));
            if (targetItem != null && targetItem != _draggedItem)
            {
                int oldIndex = FileItems.IndexOf(_draggedItem);
                int newIndex = FileItems.IndexOf(targetItem);

                if (oldIndex != -1 && newIndex != -1)
                {
                    FileItems.Move(oldIndex, newIndex);
                    SaveQueue(); // Save new order
                }
            }
            _draggedItem = null;
        }

        private FileDisplayItem? GetDataGridItemUnderMouse(System.Windows.Point pos)
        {
            var hitTestResult = VisualTreeHelper.HitTest(FilesDataGrid, pos);
            if (hitTestResult == null) return null;

            var element = hitTestResult.VisualHit;
            while (element != null && !(element is DataGridRow))
            {
                element = VisualTreeHelper.GetParent(element);
            }

            return (element as DataGridRow)?.Item as FileDisplayItem;
        }
        #endregion

        #region Download Orchestration Callbacks
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
        #endregion

        #region UI Update & Visualization
        private void UpdateMemoryUsage()
        {
            try
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                long memoryBytes = process.PrivateMemorySize64;
                MemoryUsageText.Text = $"RAM: {memoryBytes / (1024 * 1024)} MB";
            }
            catch { }
        }

        private void UpdateSummary()
        {
            long totalBytes = FileItems.Sum(i => i.Source.Size);
            
            int queued = FileItems.Count(i => i.Status == "Pending");
            int active = FileItems.Count(i => i.Status == "Downloading...");
            int finished = FileItems.Count(i => i.Status == "Completed");

            if (_lastActiveCount > 0 && active == 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            _lastActiveCount = active;

            TotalSizeText.Text = FormatSizeGB(totalBytes);
            QueuedCountText.Text = (queued + FileItems.Count(i => i.Status == "Queued")).ToString();
            ActiveCountText.Text = active.ToString();
            FinishedCountText.Text = finished.ToString();

            // Downloaded / Remaining
            long downloadedBytes = FileItems.Sum(i => i.SourceProgress?.BytesDownloaded ?? i.BytesDownloaded);
            long remainingBytes = Math.Max(0, totalBytes - downloadedBytes);
            DownloadedSizeText.Text = FormatSizeGB(downloadedBytes);
            RemainingSizeText.Text = FormatSizeGB(remainingBytes);

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

        private void SaveColumnWidths()
        {
            _settings.ColumnWidths.Clear();
            foreach (var col in FilesDataGrid.Columns)
            {
                var header = col.Header?.ToString() ?? col.DisplayIndex.ToString();
                _settings.ColumnWidths[header] = col.ActualWidth;
            }
            SettingsService.Save(_settings);
        }

        private void RestoreColumnWidths()
        {
            if (_settings.ColumnWidths.Count == 0) return;
            foreach (var col in FilesDataGrid.Columns)
            {
                var header = col.Header?.ToString() ?? col.DisplayIndex.ToString();
                if (_settings.ColumnWidths.TryGetValue(header, out double width) && width > 20)
                {
                    col.Width = new DataGridLength(width);
                }
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveColumnWidths();
            SaveQueue();
            _notifyIcon?.Dispose();
            base.OnClosing(e);
        }

        private void SaveQueue()
        {
            var dtos = FileItems.Select(item => new Services.QueueItemDto
            {
                Id = item.Source.Id,
                Name = item.Source.Name,
                Size = item.Source.Size,
                DownloadLink = item.Source.DownloadLink,
                Md5 = item.Source.Md5,
                Token = item.Source.Token,
                Status = item.Status == "Downloading..." ? "Pending" : item.Status,
                ProgressValue = item.ProgressValue,
                BytesDownloaded = item.SourceProgress?.BytesDownloaded ?? item.BytesDownloaded
            }).ToList();
            Services.QueuePersistenceService.Save(dtos);
        }

        private void RestoreQueue()
        {
            var saved = Services.QueuePersistenceService.Load();
            foreach (var dto in saved)
            {
                var goFileItem = new GoFileItem
                {
                    Id = dto.Id,
                    Name = dto.Name,
                    Size = dto.Size,
                    DownloadLink = dto.DownloadLink,
                    Md5 = dto.Md5,
                    Token = dto.Token
                };
                var displayItem = new FileDisplayItem(goFileItem)
                {
                    Status = dto.Status == "Downloading..." ? "Pending" : dto.Status,
                    ProgressValue = dto.ProgressValue,
                    BytesDownloaded = dto.BytesDownloaded,
                    ProgressText = $"{dto.ProgressValue:F1}%"
                };
                FileItems.Add(displayItem);
            }
            UpdateSummary();
        }
        #endregion
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

        public long BytesDownloaded { get; set; }

        public DownloadProgress? SourceProgress { get; set; }

        public FileDisplayItem(GoFileItem source) => Source = source;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}