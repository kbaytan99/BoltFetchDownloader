using System;
using System.Threading;
using System.Threading.Tasks;
using BoltFetch.Models;

namespace BoltFetch.Services
{
    public interface IDownloadManager
    {
        int SpeedLimitKB { get; set; }
        int SegmentsPerFile { get; set; }

        event Action<string, DownloadProgress>? ProgressChanged;
        event Action<string, string>? DownloadCompleted;
        event Action<string, string>? DownloadFailed;
        event Action<string>? DownloadCancelled;

        void UpdateParallelLimit(int limit);
        void CancelDownload(string itemId);
        void CancelAll();
        Task DownloadFileAsync(GoFileItem item, string destinationFolder, CancellationToken externalToken = default);
        void UpdateProgressFromExistingParts(GoFileItem item, string destinationFolder, DownloadProgress progress);
    }
}
