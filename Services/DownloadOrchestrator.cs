using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BoltFetch.Models;

namespace BoltFetch.Services
{
    public class DownloadOrchestrator
    {
        private readonly DownloadManager _downloadManager;
        private readonly UserSettings _settings;
        private bool _isProcessing = false;

        public event Action? StateChanged;

        public DownloadOrchestrator(DownloadManager manager, UserSettings settings)
        {
            _downloadManager = manager;
            _settings = settings;
        }

        public async Task ProcessQueueAsync(IEnumerable<FileDisplayItem> items)
        {
            if (_isProcessing) return;
            _isProcessing = true;

            try
            {
                while (true)
                {
                    var queuedItems = items.Where(i => i.Status == "Queued").ToList();
                    if (!queuedItems.Any()) break;

                    int activeCount = items.Count(i => i.Status == "Downloading...");
                    int slotsAvailable = _settings.MaxParallelDownloads - activeCount;

                    if (slotsAvailable <= 0)
                    {
                        await Task.Delay(1000);
                        continue;
                    }

                    foreach (var item in queuedItems.Take(slotsAvailable))
                    {
                        _ = StartItemDownload(item);
                    }

                    await Task.Delay(1000);
                }
            }
            finally
            {
                _isProcessing = false;
            }
        }

        private async Task StartItemDownload(FileDisplayItem item)
        {
            if (item.Status == "Downloading..." || item.Status == "Completed") return;
            
            item.Status = "Downloading...";
            StateChanged?.Invoke();
            
            try 
            {
                await _downloadManager.DownloadFileAsync(item.Source, _settings.DownloadPath);
            }
            catch
            {
                // DownloadManager handles events for failure
            }
        }
    }
}
