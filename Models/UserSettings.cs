using System;
using System.Collections.Generic;
using System.IO;

namespace BoltFetch.Models
{
    public class UserSettings
    {
        public string DownloadPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "BoltFetch");
        public int SpeedLimitKB { get; set; } = 0; // 0 = No limit
        public int MaxParallelDownloads { get; set; } = Services.SmartEngine.GetHardwareOptimizedParallelDownloads();
        public int SegmentsPerFile { get; set; } = 4;
        public string Language { get; set; } = "en";
        public Dictionary<string, double> ColumnWidths { get; set; } = new Dictionary<string, double>();
    }
}
