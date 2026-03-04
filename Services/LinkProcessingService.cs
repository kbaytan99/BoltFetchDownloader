using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BoltFetch.Models;
using BoltFetch;

namespace BoltFetch.Services;

public class LinkProcessingService : ILinkProcessingService
{
    private readonly IGoFileService _goFileService;
    private readonly IDownloadManager _downloadManager;

    public LinkProcessingService(IGoFileService goFileService, IDownloadManager downloadManager)
    {
        _goFileService = goFileService;
        _downloadManager = downloadManager;
    }

    public async Task<List<FileDisplayItem>> ProcessLinksAsync(List<string> urls, string downloadPath)
    {
        var allProcessedItems = new List<FileDisplayItem>();

        foreach (var url in urls)
        {
            var folderCode = url.Split('/').LastOrDefault();
            if (string.IsNullOrEmpty(folderCode)) continue;

            try
            {
                var items = await _goFileService.GetFolderContents(folderCode);
                Logger.Info($"LinkProcessingService: Retrieved {items.Count} items for folder {folderCode}.");

                var newDisplayItems = await Task.Run(() =>
                {
                    var processedList = new List<FileDisplayItem>();
                    foreach (var goItem in items)
                    {
                        var displayItem = new FileDisplayItem(goItem);
                        var tempProgress = new DownloadProgress { FileName = goItem.Name, TotalBytes = goItem.Size };
                        
                        // Disk I/O to check existing parts
                        _downloadManager.UpdateProgressFromExistingParts(goItem, downloadPath, tempProgress);
                        
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

                        processedList.Add(displayItem);
                    }
                    return processedList;
                });

                allProcessedItems.AddRange(newDisplayItems);
            }
            catch (Exception ex)
            {
                Logger.Error($"LinkProcessingService: Error processing {url}: {ex.Message}");
                // We rethrow or handle? For now, let's let the caller know or log it.
                // In this context, we'll log and continue to next URL.
            }
        }

        return allProcessedItems;
    }
}
