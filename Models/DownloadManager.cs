using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace BoltFetch.Models
{
    public class DownloadManager
    {
        private static readonly HttpClient _httpClient;
        private SemaphoreSlim _semaphore;
        private readonly ConcurrentDictionary<string, DownloadProgress> _progressMap = new ConcurrentDictionary<string, DownloadProgress>();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationMap = new ConcurrentDictionary<string, CancellationTokenSource>();

        private const int BUFFER_SIZE = 256 * 1024; // 256KB buffer for high-speed connections

        public int SpeedLimitKB { get; set; } = 0;
        public int SegmentsPerFile { get; set; } = 4;

        static DownloadManager()
        {
            var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 64,
                EnableMultipleHttp2Connections = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(15)
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromHours(12)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Origin", "https://gofile.io");
            _httpClient.DefaultRequestHeaders.Add("Referer", "https://gofile.io/");
        }

        public event Action<string, DownloadProgress>? ProgressChanged;
        public event Action<string, string>? DownloadCompleted;
        public event Action<string, string>? DownloadFailed;
        public event Action<string>? DownloadCancelled;

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
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // SmartEngine: auto-categorize into subfolder
                var category = Services.SmartEngine.CategorizeFile(item.Name);
                var finalFolder = Path.Combine(destinationFolder, category);
                if (!Directory.Exists(finalFolder)) Directory.CreateDirectory(finalFolder);

                var destinationPath = Path.Combine(finalFolder, item.Name);

                // SmartEngine: get optimal segment count
                var serverDomain = "";
                try { serverDomain = new Uri(item.DownloadLink).Host; } catch { }
                int smartSegments = Services.SmartEngine.GetOptimalSegments(item.Size, serverDomain, SegmentsPerFile);

                var progress = new DownloadProgress { FileName = item.Name, TotalBytes = item.Size };
                _progressMap[item.Id] = progress;

                // Sync existing bytes for UI
                UpdateProgressFromExistingParts(item, finalFolder, progress);
                ProgressChanged?.Invoke(item.Id, progress);

                // 1. Check if server supports Range requests
                bool supportsRange = await CheckRangeSupport(item.DownloadLink, item.Token, cts.Token);
                
                int savedSegments = SegmentsPerFile;
                SegmentsPerFile = smartSegments;
                try
                {
                    if (supportsRange && item.Size > 1024 * 1024 && SegmentsPerFile > 1)
                    {
                        string tempPath = destinationPath + ".downloading";
                        await DownloadSegmentedAsync(item, tempPath, progress, cts.Token);
                        if (File.Exists(destinationPath)) File.Delete(destinationPath);
                        File.Move(tempPath, destinationPath);

                        // Clean up state file on success
                        var statePath = tempPath + ".state";
                        if (File.Exists(statePath)) File.Delete(statePath);
                    }
                    else
                    {
                        await DownloadSingleStreamAsync(item, destinationPath, progress, cts.Token, supportsRange);
                    }
                }
                finally { SegmentsPerFile = savedSegments; }

                stopwatch.Stop();

                // SmartEngine: log for future optimization
                Services.SmartEngine.LogDownload(new Services.DownloadRecord
                {
                    FileName = item.Name,
                    FileSize = item.Size,
                    ServerDomain = serverDomain,
                    SegmentsUsed = smartSegments,
                    AverageSpeedMBps = item.Size / (1024.0 * 1024) / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.1),
                    DurationSeconds = stopwatch.Elapsed.TotalSeconds,
                    Category = category
                });

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
            if (File.Exists(destinationPath)) 
            {
                existing = new FileInfo(destinationPath).Length;
            }
            
            // Segmented case (check state file)
            var statePath = destinationPath + ".downloading.state";
            if (File.Exists(statePath))
            {
                try
                {
                    var stateJson = File.ReadAllText(statePath);
                    var state = JsonConvert.DeserializeObject<List<int>>(stateJson);
                    if (state != null)
                    {
                        const long CHUNK_SIZE = 4 * 1024 * 1024;
                        existing = (long)state.Count * CHUNK_SIZE;
                    }
                }
                catch { }
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

            HttpResponseMessage? response = null;
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, item.DownloadLink);
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

                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    response.Dispose();
                    if (attempt == 5) throw new HttpRequestException("429 Too Many Requests after 5 limits reached.");
                    await Task.Delay(2000 * attempt, cancellationToken);
                    continue;
                }
                break;
            }

            if (response == null) throw new HttpRequestException("Failed to get response after retries.");
            response.EnsureSuccessStatusCode();

            using (response)
            {
                var mode = (existingLength > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent) 
                            ? FileMode.Append : FileMode.Create;

                using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                using (var fileStream = new FileStream(destinationPath, mode, FileAccess.Write, FileShare.None, BUFFER_SIZE, true))
                {
                    await CopyWithReporting(stream, fileStream, item.Id, progress, cancellationToken);
                }
            }
        }

        private struct FileWriteRequest
        {
            public byte[] Data;
            public long Position;
            public int Length;
        }

        private async Task DownloadSegmentedAsync(GoFileItem item, string destinationPath, DownloadProgress progress, CancellationToken cancellationToken)
        {
            // --- RESUMPTION LOGIC: LOAD STATE ---
            var statePath = destinationPath + ".state";
            var completedChunks = new HashSet<int>();
            if (File.Exists(statePath))
            {
                try
                {
                    var stateJson = File.ReadAllText(statePath);
                    var state = JsonConvert.DeserializeObject<List<int>>(stateJson);
                    if (state != null) completedChunks = new HashSet<int>(state);
                }
                catch { }
            }

            const long CHUNK_SIZE = 4 * 1024 * 1024; // 4MB chunks
            var chunks = new ConcurrentQueue<(long start, long end, int index)>();
            int totalChunks = (int)Math.Ceiling((double)item.Size / CHUNK_SIZE);

            for (int i = 0; i < totalChunks; i++)
            {
                if (completedChunks.Contains(i)) continue;

                long start = (long)i * CHUNK_SIZE;
                long end = Math.Min(start + CHUNK_SIZE - 1, item.Size - 1);
                chunks.Enqueue((start, end, i));
            }

            // Update initial progress based on completed chunks
            progress.BytesDownloaded = (long)completedChunks.Count * CHUNK_SIZE;
            if (progress.BytesDownloaded > item.Size) progress.BytesDownloaded = item.Size;

            // --- TURBO BOOSTER: BUFFERED ASYNC WRITER ---
            var writeChannel = Channel.CreateBounded<FileWriteRequest>(new BoundedChannelOptions(256) 
            { 
                FullMode = BoundedChannelFullMode.Wait 
            });

            var writerTask = Task.Run(async () => 
            {
                using var fs = new FileStream(destinationPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, BUFFER_SIZE, true);
                if (fs.Length != item.Size) fs.SetLength(item.Size);
                
                await foreach (var req in writeChannel.Reader.ReadAllAsync(cancellationToken))
                {
                    fs.Seek(req.Position, SeekOrigin.Begin);
                    await fs.WriteAsync(req.Data.AsMemory(0, req.Length), cancellationToken);
                    System.Buffers.ArrayPool<byte>.Shared.Return(req.Data);
                }
            });

            var downloadTasks = new List<Task>();
            object stateLock = new object();

            for (int i = 0; i < SegmentsPerFile; i++)
            {
                downloadTasks.Add(Task.Run(async () =>
                {
                    var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
                    try
                    {
                        while (chunks.TryDequeue(out var chunk))
                        {
                        try
                        {
                            HttpResponseMessage? response = null;
                            for (int attempt = 1; attempt <= 5; attempt++)
                            {
                                var request = new HttpRequestMessage(HttpMethod.Get, item.DownloadLink);
                                request.Headers.Add("Authorization", $"Bearer {item.Token}");
                                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(chunk.start, chunk.end);

                                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                                
                                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                                {
                                    response.Dispose();
                                    if (attempt == 5) throw new HttpRequestException("429 Too Many Requests after 5 limits reached.");
                                    await Task.Delay(2000 * attempt + Random.Shared.Next(100, 1000), cancellationToken);
                                    continue;
                                }
                                break;
                            }

                            if (response == null) throw new HttpRequestException("Failed to get response after retries.");
                            response.EnsureSuccessStatusCode();

                            using (response)
                            using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                            {
                                int read;
                                long currentPos = chunk.start;

                                while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                                {
                                    var dataCopy = System.Buffers.ArrayPool<byte>.Shared.Rent(read);
                                    Buffer.BlockCopy(buffer, 0, dataCopy, 0, read);
                                    
                                    await writeChannel.Writer.WriteAsync(new FileWriteRequest 
                                    { 
                                        Data = dataCopy, 
                                        Position = currentPos, 
                                        Length = read 
                                    }, cancellationToken);

                                    currentPos += read;
                                    progress.AddBytes(read);
                                    Services.Speedometer.AddBytes(read);

                                    if (SpeedLimitKB > 0)
                                        await ThrottleInstant(read, SpeedLimitKB, cancellationToken);
                                }

                                // Chunk finished, mark as complete
                                lock(stateLock)
                                {
                                    completedChunks.Add(chunk.index);
                                    // Save state periodically (e.g., every 5 chunks or every few seconds)
                                    if (completedChunks.Count % 5 == 0)
                                    {
                                        var json = JsonConvert.SerializeObject(completedChunks.ToList());
                                        File.WriteAllText(statePath, json);
                                    }
                                }
                            }
                        }
                        catch (Exception)
                        {
                            throw;
                        }
                    }
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                    }
                }, cancellationToken));
            }

            // Reporting loop
            var reportingTask = Task.Run(async () =>
            {
                while (!downloadTasks.All(t => t.IsCompleted))
                {
                    progress.UpdateInstantSpeed();
                    ProgressChanged?.Invoke(item.Id, progress);
                    await Task.Delay(500, cancellationToken);
                }
            }, cancellationToken);

            try
            {
                await Task.WhenAll(downloadTasks);
            }
            finally
            {
                writeChannel.Writer.Complete();
            }
            await writerTask;
        }

        private async Task CopyWithReporting(Stream source, Stream destination, string itemId, DownloadProgress progress, CancellationToken cancellationToken)
        {
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                int bytesRead;
                var lastReportTime = DateTime.MinValue;

                while ((bytesRead = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                progress.AddBytes(bytesRead);
                Services.Speedometer.AddBytes(bytesRead);

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
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
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
        public string FileName { get; set; } = string.Empty;
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
