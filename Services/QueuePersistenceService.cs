using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BoltFetch.Services
{
    public class QueueItemDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public long Size { get; set; }
        public string DownloadLink { get; set; } = string.Empty;
        public string Md5 { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string Status { get; set; } = "Pending";
        public double ProgressValue { get; set; }
        public long BytesDownloaded { get; set; }
    }

    public static class QueuePersistenceService
    {
        private static readonly string QueueFilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "download_queue.json");

        public static void Save(IEnumerable<QueueItemDto> items)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(items, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllBytes(QueueFilePath, bytes);
            }
            catch { }
        }

        public static List<QueueItemDto> Load()
        {
            try
            {
                if (File.Exists(QueueFilePath))
                {
                    var bytes = File.ReadAllBytes(QueueFilePath);
                    return JsonSerializer.Deserialize<List<QueueItemDto>>(bytes) ?? new List<QueueItemDto>();
                }
            }
            catch { }
            return new List<QueueItemDto>();
        }
    }
}
