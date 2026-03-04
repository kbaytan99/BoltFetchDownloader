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
using System.Reflection;
using Forms = System.Windows.Forms;

namespace BoltFetch.Views
{
    public partial class MainWindow : Window
    {
        private readonly IGoFileService _goFileService;
        private readonly IDownloadManager _downloadManager;
        private readonly ISettingsService _settingsService;
        private readonly ITrayIconService _trayIconService;
        private readonly ILinkProcessingService _linkProcessingService;
        private readonly IDownloadCleanupService _downloadCleanupService;
        public ViewModels.MainViewModel ViewModel { get; }

        private UserSettings _settings;
        private readonly ClipboardMonitor _clipboardMonitor = new ClipboardMonitor();
        private readonly NotificationService _notificationService = new NotificationService();
        private readonly DownloadOrchestrator _orchestrator;
        
        public BulkObservableCollection<FileDisplayItem> FileItems => ViewModel.FileItems;
        
        // Speed Graph fields
        private readonly List<double> _speedHistory = new List<double>();
        private int _lastActiveCount = 0;
        private const int MaxHistoryPoints = 60;
        private DateTime _lastGraphUpdate = DateTime.MinValue;
        private System.Windows.Point _dragStartPoint;
        private FileDisplayItem? _draggedItem;

        public MainWindow(ViewModels.MainViewModel viewModel, 
            ISettingsService settingsService, 
            IDownloadManager downloadManager, 
            IGoFileService goFileService, 
            ITrayIconService trayIconService,
            ILinkProcessingService linkProcessingService,
            IDownloadCleanupService downloadCleanupService)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = ViewModel;
            
            _settingsService = settingsService;
            _goFileService = goFileService;
            _downloadManager = downloadManager;
            _trayIconService = trayIconService;
            _linkProcessingService = linkProcessingService;
            _downloadCleanupService = downloadCleanupService;

            _settings = _settingsService.Load();
            _orchestrator = new DownloadOrchestrator((DownloadManager)_downloadManager, _settings);
            
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

            // Setup NotifyIcon via Service
            _trayIconService.Initialize(this, () => SettingsButton_Click(null!, null!), () => ShowAboutWindow());

            // Load saved language
            ApplyLanguage(_settings.Language);

            // Restore column widths and saved download queue
            RestoreColumnWidths();
            RestoreColumnWidths();
            RestoreQueue();
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

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.V && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    string text = System.Windows.Clipboard.GetText().Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        var matches = Regex.Matches(text, @"(?:https?://)?(?:www\.)?gofile\.io/d/[a-zA-Z0-9\-]+", RegexOptions.IgnoreCase);
                        if (matches.Count > 0)
                        {
                            var links = new string[matches.Count];
                            for (int i = 0; i < matches.Count; i++) links[i] = matches[i].Value;
                            
                            // Prevent overlapping checks or double-calls if monitor fires at the exact same moment
                            HandleDetectedLinks(links);
                        }
                    }
                }
            }
        }

        #region System Tray & About logic
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
                _trayIconService.HideToTray();
            }
            base.OnStateChanged(e);
        }

        #endregion

        #region General UI Methods

        private void HistoryButton_Click(object sender, RoutedEventArgs e)
        {
            var win = new HistoryWindow();
            win.Owner = this;
            win.ShowDialog();
        }

        private void MonitorButton_Click(object sender, RoutedEventArgs e)
        {
            var win = new HardwareMonitorWindow();
            win.Owner = this;
            win.ShowDialog();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var oldLang = _settings.Language;
            var settingsWindow = new SettingsWindow(_settings);
            settingsWindow.Owner = this;
            if (settingsWindow.ShowDialog() == true)
            {
                _settings = settingsWindow.Settings;
                _settingsService.Save(_settings);
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

            // Remove old locale dictionaries and add the new one to Application resources
            var toRemove = System.Windows.Application.Current.Resources.MergedDictionaries
                .Where(d => d.Source != null && d.Source.OriginalString.StartsWith("Locales/")).ToList();
            foreach (var d in toRemove) System.Windows.Application.Current.Resources.MergedDictionaries.Remove(d);
            System.Windows.Application.Current.Resources.MergedDictionaries.Add(dict);

            // Re-apply the dynamic version string
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
            if (version.Contains('+')) version = version[..version.IndexOf('+')];
            var versionPrefix = langCode == "tr" ? "Sürüm" : "Version";
            System.Windows.Application.Current.Resources["Loc_AppVersion"] = $"{versionPrefix} {version}";

            // Refresh UI elements that don't auto-update (like DataGrid Headers)
            RefreshWindowHeaders(this);
            
            foreach (Window win in System.Windows.Application.Current.Windows)
            {
                if (win != this) RefreshWindowHeaders(win);
            }

            UpdateSummary();
        }

        private void RefreshWindowHeaders(Window win)
        {
            if (win == null) return;

            // Find DataGrids in the window and refresh their headers
            if (win is MainWindow mainWin && mainWin.FilesDataGrid != null)
            {
                string[] headerKeys = { "Loc_ColName", "Loc_ColSize", "Loc_ColProgress", "Loc_ColSpeed", "Loc_ColETA", "Loc_ColStatus", "Loc_ColControls" };
                for (int i = 0; i < mainWin.FilesDataGrid.Columns.Count && i < headerKeys.Length; i++)
                {
                    var header = win.TryFindResource(headerKeys[i]);
                    if (header != null) mainWin.FilesDataGrid.Columns[i].Header = header;
                }
            }
            else if (win is HistoryWindow histWin && histWin.HistoryDataGrid != null)
            {
                string[] histKeys = { "Loc_HistColDate", "Loc_HistColName", "Loc_HistColSize", "Loc_HistColCat" };
                for (int i = 0; i < histWin.HistoryDataGrid.Columns.Count && i < histKeys.Length; i++)
                {
                    var header = win.TryFindResource(histKeys[i]);
                    if (header != null) histWin.HistoryDataGrid.Columns[i].Header = header;
                }
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
            if (urls == null || !urls.Any()) return;
            
            Dispatcher.Invoke(() => AddButton.IsEnabled = false);

            try
            {
                var processedItems = await _linkProcessingService.ProcessLinksAsync(urls, _settings.DownloadPath);
                
                // deduplication against current items safely
                var existingIds = new HashSet<string>(FileItems.Select(i => i.Source.Id));
                var newItems = processedItems.Where(i => !existingIds.Contains(i.Source.Id)).ToList();

                if (newItems.Any())
                {
                    await Dispatcher.InvokeAsync(() => {
                        FileItems.AddRange(newItems);
                        long totalSize = newItems.Sum(i => i.Source.Size);
                        _notificationService.ShowPopup($"{newItems.Count} Files Found! ??", $"{newItems.Count} files ({FormatSizeGB(totalSize)}) added to the list.");
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                string contexts = "Attempted URLs:\n" + string.Join("\n", urls);
                Dispatcher.Invoke(() => _notificationService.ShowMessage($"Error fetching folder content: {ex.Message}", context: contexts));
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
                // Account for SmartEngine smart paths (Category/FileName/)
                var smartPath = Services.SmartEngine.GetSmartPath(item.Name);
                var filePath = Path.Combine(_settings.DownloadPath, smartPath, item.Name);
                
                // Fallback to base path if not in category
                if (!File.Exists(filePath))
                    filePath = Path.Combine(_settings.DownloadPath, item.Name);

                if (File.Exists(filePath))
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
                }
                else
                {
                    var title = FindResource("Loc_FileNotFoundTitle") as string ?? "File Not Found";
                    var msg = FindResource("Loc_FileNotFoundMsg") as string ?? "File not found on disk. Remove from list?";
                    var btnText = (FindResource("Loc_RowRemove") as string ?? "Remove").Replace("?", "").Trim();

                    if (ConfirmDialog.Show(this, title, msg, btnText, true))
                    {
                        FileItems.Remove(item);
                        UpdateSummary();
                    }
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
            if (sender is FrameworkElement { DataContext: FileDisplayItem item })
            {
                if (ConfirmDialog.Show(this, "Permanent Deletion", $"PERMANENTLY delete '{item.Name}' from disk AND list?\n\nThis cannot be undone.", "Delete", isDanger: true))
                {
                    _downloadManager.CancelDownload(item.Source.Id);
                    
                    try 
                    {
                        _downloadCleanupService.DeleteDownloadFiles(_settings.DownloadPath, item.Name);
                    }
                    catch (Exception ex)
                    {
                        _notificationService.ShowMessage($"Error deleting files: {ex.Message}");
                    }

                    FileItems.Remove(item);
                    UpdateSummary();
                }
            }
        }

        // Deleted internal method - functionality moved to DownloadCleanupService

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            FilesDataGrid.SelectAll();
        }

        private void StopAll_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.StopAllCommand.Execute(null);
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
            
            if (!targetItems.Any()) return;

            string prompt = targetItems.Count == FileItems.Count 
                ? $"Are you sure you want to PERMANENTLY delete ALL {FileItems.Count} items from disk AND list?"
                : $"Are you sure you want to PERMANENTLY delete the selected {targetItems.Count} items from disk AND list?";

            if (ConfirmDialog.Show(this, "Permanent Deletion", prompt + "\n\nThis cannot be undone.", "Delete All", isDanger: true))
            {
                foreach (var item in targetItems)
                {
                    _downloadManager.CancelDownload(item.Source.Id);
                    try { _downloadCleanupService.DeleteDownloadFiles(_settings.DownloadPath, item.Name); } catch { /* ignore in bulk delete or log */ }
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
        private void UpdateSummary()
        {
            long totalBytes = FileItems.Sum(i => i.Source.Size);
            
            int queued = FileItems.Count(i => i.Status == "Pending");
            int active = FileItems.Count(i => i.Status == "Downloading...");
            int finished = FileItems.Count(i => i.Status == "Completed");
            _lastActiveCount = active;

            TotalSizeText.Text = FormatSizeGB(totalBytes);
            QueuedCountText.Text = (queued + FileItems.Count(i => i.Status == "Queued")).ToString();
            ActiveCountText.Text = active.ToString();
            FinishedCountText.Text = finished.ToString();

            // Downloaded / Remaining
            long downloadedBytes = FileItems.Sum(i => i.Status == "Completed" ? i.Source.Size : (i.SourceProgress?.BytesDownloaded ?? i.BytesDownloaded));
            long remainingBytes = Math.Max(0, totalBytes - downloadedBytes);
            DownloadedSizeText.Text = FormatSizeGB(downloadedBytes);
            RemainingSizeText.Text = FormatSizeGB(remainingBytes);

            // Calculate Total Speed using real-time isolated Speedometer
            long totalSpeedBytes = BoltFetch.Services.Speedometer.GetCurrentSpeed();
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
            _settingsService.Save(_settings);
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
            _trayIconService?.Dispose();
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
