using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

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
                var json = JsonConvert.SerializeObject(items, Formatting.Indented);
                File.WriteAllText(QueueFilePath, json);
            }
            catch { }
        }

        public static List<QueueItemDto> Load()
        {
            try
            {
                if (File.Exists(QueueFilePath))
                {
                    var json = File.ReadAllText(QueueFilePath);
                    return JsonConvert.DeserializeObject<List<QueueItemDto>>(json) ?? new List<QueueItemDto>();
                }
            }
            catch { }
            return new List<QueueItemDto>();
        }
    }
}
