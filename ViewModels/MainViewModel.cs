using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BoltFetch.Models;
using BoltFetch.Services;

namespace BoltFetch.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly IDownloadManager _downloadManager;
        private readonly IGoFileService _goFileService;
        private readonly ISettingsService _settingsService;
        
        public BulkObservableCollection<FileDisplayItem> FileItems { get; } = new BulkObservableCollection<FileDisplayItem>();

        [ObservableProperty]
        private string _totalSizeText = "0.00 GB";

        [ObservableProperty]
        private string _queuedCountText = "0";

        [ObservableProperty]
        private string _activeCountText = "0";

        [ObservableProperty]
        private string _finishedCountText = "0";

        [ObservableProperty]
        private string _downloadedSizeText = "0.00 GB";

        [ObservableProperty]
        private string _remainingSizeText = "0.00 GB";

        [ObservableProperty]
        private string _totalSpeedText = "0 KB/s";

        [ObservableProperty]
        private string _parallelLimitText = "3";

        [ObservableProperty]
        private string _pathText = "...";

        public MainViewModel(IDownloadManager downloadManager, IGoFileService goFileService, ISettingsService settingsService)
        {
            _downloadManager = downloadManager;
            _goFileService = goFileService;
            _settingsService = settingsService;
        }

        public void UpdateSummary()
        {
            int pending = 0, active = 0, completed = 0;
            long totalSize = 0, downloaded = 0;

            foreach (var item in FileItems)
            {
                totalSize += item.Source.Size;
                downloaded += item.BytesDownloaded;

                if (item.Status == "Completed") completed++;
                else if (item.Status == "Downloading..." || item.Status == "Pending") active++;
                else pending++;
            }

            TotalSizeText = FormatSizeGB(totalSize);
            DownloadedSizeText = FormatSizeGB(downloaded);
            
            long rem = totalSize - downloaded;
            RemainingSizeText = rem > 0 ? FormatSizeGB(rem) : "0.00 GB";

            QueuedCountText = pending.ToString();
            ActiveCountText = active.ToString();
            FinishedCountText = completed.ToString();
        }

        private string FormatSizeGB(long bytes)
        {
            double gb = (double)bytes / (1024 * 1024 * 1024);
            return $"{gb:F2} GB";
        }

        [RelayCommand]
        private void StopAll()
        {
            foreach (var item in FileItems.Where(i => i.IsNotCompleted))
            {
                item.Status = "Stopped";
                item.SpeedText = "-";
                item.ETAText = "--:--";
            }
            _downloadManager.CancelAll();
            UpdateSummary();
        }

        [RelayCommand]
        private void RemoveAll()
        {
            var completedItems = FileItems.Where(i => i.IsCompleted).ToList();
            foreach (var item in completedItems)
            {
                FileItems.Remove(item);
            }
            UpdateSummary();
        }
    }
}
