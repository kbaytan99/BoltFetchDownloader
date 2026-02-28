using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BoltFetch.Models
{
    public class DownloadManager
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private SemaphoreSlim _semaphore;
        private readonly ConcurrentDictionary<string, DownloadProgress> _progressMap = new ConcurrentDictionary<string, DownloadProgress>();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationMap = new ConcurrentDictionary<string, CancellationTokenSource>();

        public int SpeedLimitKB { get; set; } = 0;
        public int SegmentsPerFile { get; set; } = 4;

        static DownloadManager()
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Origin", "https://gofile.io");
            _httpClient.DefaultRequestHeaders.Add("Referer", "https://gofile.io/");
        }

        public event Action<string, DownloadProgress> ProgressChanged;
        public event Action<string, string> DownloadCompleted;
        public event Action<string, string> DownloadFailed;
        public event Action<string> DownloadCancelled;

        public DownloadManager(int maxParallelDownloads = 3)
        {
            _semaphore = new SemaphoreSlim(maxParallelDownloads);
        }

        public void UpdateParallelLimit(int limit)
        {
            // Note: SemaphoreSlim doesn't support resizing. 
            // We create a new one for FUTURE tasks. Existing ones will finish.
            _semaphore = new SemaphoreSlim(limit);
        }

        public void CancelDownload(string itemId)
        {
            if (_cancellationMap.TryRemove(itemId, out var cts))
            {
                cts.Cancel();
                DownloadCancelled?.Invoke(itemId);
            }
        }

        public async Task DownloadFileAsync(GoFileItem item, string destinationFolder, CancellationToken externalToken = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            _cancellationMap[item.Id] = cts;

            await _semaphore.WaitAsync(cts.Token);
            try
            {
                var destinationPath = Path.Combine(destinationFolder, item.Name);
                if (!Directory.Exists(destinationFolder)) Directory.CreateDirectory(destinationFolder);

                var progress = new DownloadProgress { FileName = item.Name, TotalBytes = item.Size };
                _progressMap[item.Id] = progress;

                // Sync existing bytes for UI
                UpdateProgressFromExistingParts(item, destinationFolder, progress);
                ProgressChanged?.Invoke(item.Id, progress);

                // 1. Check if server supports Range requests
                bool supportsRange = await CheckRangeSupport(item.DownloadLink, item.Token, cts.Token);
                
                if (supportsRange && item.Size > 1024 * 1024 && SegmentsPerFile > 1)
                {
                    await DownloadSegmentedAsync(item, destinationPath, progress, cts.Token);
                }
                else
                {
                    await DownloadSingleStreamAsync(item, destinationPath, progress, cts.Token, supportsRange);
                }

                DownloadCompleted?.Invoke(item.Id, destinationPath);
            }
            catch (OperationCanceledException)
            {
                DownloadCancelled?.Invoke(item.Id);
            }
            catch (Exception ex)
            {
                DownloadFailed?.Invoke(item.Id, ex.Message);
            }
            finally
            {
                _cancellationMap.TryRemove(item.Id, out _);
                _semaphore.Release();
            }
        }

        public void UpdateProgressFromExistingParts(GoFileItem item, string destinationFolder, DownloadProgress progress)
        {
            long existing = 0;
            var destinationPath = Path.Combine(destinationFolder, item.Name);
            
            // Single stream case
            if (File.Exists(destinationPath)) existing = new FileInfo(destinationPath).Length;
            
            // Segmented case (check parts)
            // Even if the user changes SegmentsPerFile later, we scan up to a reasonable limit or the current setting
            // To be safe, we check at least up to the current SegmentsPerFile
            for (int i = 0; i < Math.Max(SegmentsPerFile, 32); i++) 
            {
                var partPath = destinationPath + ".part" + (i + 1);
                if (File.Exists(partPath)) existing += new FileInfo(partPath).Length;
            }

            progress.BytesDownloaded = Math.Min(existing, item.Size);
        }

        private async Task<bool> CheckRangeSupport(string url, string token, CancellationToken tokenSource)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                request.Headers.Add("Authorization", $"Bearer {token}");
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, tokenSource);
                return response.Headers.AcceptRanges.Contains("bytes");
            }
            catch { return false; }
        }

        private async Task DownloadSingleStreamAsync(GoFileItem item, string destinationPath, DownloadProgress progress, CancellationToken cancellationToken, bool supportsRange)
        {
            long existingLength = 0;
            if (supportsRange && File.Exists(destinationPath))
            {
                existingLength = new FileInfo(destinationPath).Length;
                if (existingLength >= item.Size)
                {
                    progress.BytesDownloaded = item.Size;
                    ProgressChanged?.Invoke(item.Id, progress);
                    return; // Already done
                }
            }

            using (var request = new HttpRequestMessage(HttpMethod.Get, item.DownloadLink))
            {
                request.Headers.Add("Authorization", $"Bearer {item.Token}");
                if (existingLength > 0)
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingLength, null);
                    progress.BytesDownloaded = existingLength;
                }
                else
                {
                    progress.BytesDownloaded = 0;
                }

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    
                    var mode = (existingLength > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent) 
                               ? FileMode.Append : FileMode.Create;

                    using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                    using (var fileStream = new FileStream(destinationPath, mode, FileAccess.Write, FileShare.None, 16384, true))
                    {
                        await CopyWithReporting(stream, fileStream, item.Id, progress, cancellationToken);
                    }
                }
            }
        }

        private async Task DownloadSegmentedAsync(GoFileItem item, string destinationPath, DownloadProgress progress, CancellationToken cancellationToken)
        {
            var segmentSize = item.Size / SegmentsPerFile;
            var tasks = new List<Task>();
            var bytesDownloadedPerSegment = new long[SegmentsPerFile];

            for (int i = 0; i < SegmentsPerFile; i++)
            {
                var segmentIndex = i;
                tasks.Add(Task.Run(async () =>
                {
                    var partPath = destinationPath + ".part" + (segmentIndex + 1);
                    long startOffset = segmentIndex * segmentSize;
                    long endOffset = (segmentIndex == SegmentsPerFile - 1) ? item.Size - 1 : startOffset + segmentSize - 1;
                    
                    long existingPartSize = 0;
                    if (File.Exists(partPath)) existingPartSize = new FileInfo(partPath).Length;
                    
                    if (existingPartSize >= (endOffset - startOffset + 1))
                    {
                        bytesDownloadedPerSegment[segmentIndex] = existingPartSize;
                        return; // Segment complete
                    }

                    using (var request = new HttpRequestMessage(HttpMethod.Get, item.DownloadLink))
                    {
                        request.Headers.Add("Authorization", $"Bearer {item.Token}");
                        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(startOffset + existingPartSize, endOffset);

                        using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                        {
                            response.EnsureSuccessStatusCode();
                            var mode = existingPartSize > 0 ? FileMode.Append : FileMode.Create;
                            
                            using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                            using (var fileStream = new FileStream(partPath, mode, FileAccess.Write, FileShare.None, 16384, true))
                            {
                                var buffer = new byte[16384];
                                int read;
                                bytesDownloadedPerSegment[segmentIndex] = existingPartSize;

                                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                                    {
                                        await fileStream.WriteAsync(buffer, 0, read, cancellationToken);
                                        bytesDownloadedPerSegment[segmentIndex] += read;
                                        
                                        // Periodic reporting update
                                        progress.AddBytes(read);
                                        
                                        if (SpeedLimitKB > 0) 
                                        {
                                            await ThrottleInstant(read, SpeedLimitKB, cancellationToken);
                                        }
                                    }
                            }
                        }
                    }
                }, cancellationToken));
            }

            // Reporting loop
            var reportingTask = Task.Run(async () =>
            {
                while (!tasks.All(t => t.IsCompleted))
                {
                    progress.UpdateInstantSpeed();
                    ProgressChanged?.Invoke(item.Id, progress);
                    await Task.Delay(500, cancellationToken);
                }
            }, cancellationToken);

            await Task.WhenAll(tasks);

            // Merge segments
            await MergeSegmentsAsync(destinationPath, item.Size);
        }

        private async Task MergeSegmentsAsync(string destinationPath, long totalSize)
        {
            using (var finalFs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                finalFs.SetLength(totalSize);
                for (int i = 0; i < SegmentsPerFile; i++)
                {
                    var partPath = destinationPath + ".part" + (i + 1);
                    if (!File.Exists(partPath)) continue;

                    using (var partFs = new FileStream(partPath, FileMode.Open, FileAccess.Read))
                    {
                        await partFs.CopyToAsync(finalFs);
                    }
                    File.Delete(partPath);
                }
            }
        }

        private async Task CopyWithReporting(Stream source, Stream destination, string itemId, DownloadProgress progress, CancellationToken cancellationToken)
        {
            var buffer = new byte[16384];
            int bytesRead;
            var lastReportTime = DateTime.MinValue;

            while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                progress.AddBytes(bytesRead);

                if (SpeedLimitKB > 0) 
                {
                    await ThrottleInstant(bytesRead, SpeedLimitKB, cancellationToken);
                }

                if ((DateTime.Now - lastReportTime).TotalMilliseconds > 500)
                {
                    progress.UpdateInstantSpeed();
                    ProgressChanged?.Invoke(itemId, progress);
                    lastReportTime = DateTime.Now;
                }
            }
        }

        private async Task ThrottleInstant(int bytesRead, int limitKB, CancellationToken token)
        {
            // Simple delay based on current read chunk to aim for global limit
            // This is per-segment, so it's not perfect but react instantly to 'limitKB' changes
            double expectedTimeMs = (bytesRead / (double)(limitKB * 1024)) * 1000;
            if (expectedTimeMs > 1) // Only delay if it matters
            {
                await Task.Delay((int)expectedTimeMs, token);
            }
        }
    }

    public class DownloadProgress
    {
        public string FileName { get; set; }
        public long BytesDownloaded { get; set; }
        public long TotalBytes { get; set; }
        public long SpeedBytesPerSecond { get; set; }
        public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();

        // For Instant Speed (Sliding Window)
        private long _recentBytes = 0;
        private Stopwatch _recentStopwatch = Stopwatch.StartNew();
        private readonly object _lock = new object();

        public void AddBytes(int count)
        {
            lock (_lock)
            {
                BytesDownloaded += count;
                _recentBytes += count;
            }
        }

        public void UpdateInstantSpeed()
        {
            lock (_lock)
            {
                double elapsed = _recentStopwatch.Elapsed.TotalSeconds;
                if (elapsed >= 0.5) // Every 500ms
                {
                    SpeedBytesPerSecond = (long)(_recentBytes / elapsed);
                    _recentBytes = 0;
                    _recentStopwatch.Restart();
                }
            }
        }

        public double ProgressPercentage => TotalBytes > 0 ? (double)BytesDownloaded / TotalBytes * 100 : 0;
        public string ProgressText => $"{Math.Min(ProgressPercentage, 100):F1}%";
        public string SpeedText => BytesDownloaded >= TotalBytes ? "-" : $"{FormatSize(SpeedBytesPerSecond)}/s";
        
        public string ETAText
        {
            get
            {
                if (BytesDownloaded >= TotalBytes) return "Done";
                if (SpeedBytesPerSecond <= 100) return "--:--"; // Too slow to estimate
                var remainingBytes = TotalBytes - BytesDownloaded;
                var secondsRemaining = (double)remainingBytes / SpeedBytesPerSecond;
                var timeSpan = TimeSpan.FromSeconds(secondsRemaining);
                if (timeSpan.TotalHours >= 1) return $"{(int)timeSpan.TotalHours}h {timeSpan.Minutes}m";
                return $"{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";
            }
        }

        private string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unitIndex = 0;
            while (size >= 1024 && unitIndex < units.Length - 1) { size /= 1024; unitIndex++; }
            return $"{size:F2} {units[unitIndex]}";
        }
    }
}
