using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace BoltFetch.Services
{
    #region Models
    public class DownloadRecord
    {
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string ServerDomain { get; set; } = string.Empty;
        public int SegmentsUsed { get; set; }
        public double AverageSpeedMBps { get; set; }
        public double DurationSeconds { get; set; }
        public string Category { get; set; } = "Other";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class SmartProfile
    {
        public List<DownloadRecord> History { get; set; } = new();
        public Dictionary<string, string> UserCategoryOverrides { get; set; } = new();
    }
    #endregion

    public static class SmartEngine
    {
        private static readonly string ProfilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "smart_profile.json");

        private static SmartProfile _profile = new();

        #region Category Rules
        private static readonly Dictionary<string, string[]> ExtensionMap = new()
        {
            ["Archives"]     = new[] { ".rar", ".zip", ".7z", ".tar", ".gz", ".xz", ".bz2" },
            ["Videos"]       = new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v" },
            ["Music"]        = new[] { ".mp3", ".flac", ".wav", ".aac", ".ogg", ".wma", ".m4a" },
            ["Software"]     = new[] { ".exe", ".msi", ".dmg", ".pkg", ".deb", ".rpm", ".appimage" },
            ["Disk Images"]  = new[] { ".iso", ".img", ".bin", ".cue", ".nrg" },
            ["Documents"]    = new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".epub" },
            ["Images"]       = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg", ".psd" },
        };

        // Known scene/repack groups → Game
        private static readonly string[] GameKeywords = new[]
        {
            "fitgirl", "dodi", "elamigos", "codex", "plaza", "skidrow", "reloaded",
            "gog", "repack", "steamrip", "cpy", "empress", "goldberg", "tenoke",
            "rune", "flt", "razor1911", "tinyiso", "kaoskrew", "darksiders"
        };

        // Media release keywords → Video
        private static readonly string[] VideoKeywords = new[]
        {
            "1080p", "720p", "2160p", "4k", "bluray", "bdrip", "webrip", "webdl",
            "web-dl", "hdtv", "x264", "x265", "hevc", "aac", "dts", "remux",
            "yify", "rarbg", "amzn", "nf.", "hulu"
        };

        // Software keywords
        private static readonly string[] SoftwareKeywords = new[]
        {
            "setup", "install", "portable", "patch", "crack", "keygen", "activator",
            "adobe", "microsoft", "autodesk", "vmware", "jetbrains"
        };
        #endregion

        static SmartEngine()
        {
            Load();
        }

        /// <summary>
        /// Categorize a file based on its name using heuristic rules.
        /// Returns a subfolder name like "Games", "Videos", "Software", etc.
        /// </summary>
        public static string CategorizeFile(string fileName)
        {
            // Check user overrides first
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            var nameLower = fileName.ToLowerInvariant();

            // Check user overrides (learned from corrections)
            foreach (var kvp in _profile.UserCategoryOverrides)
            {
                if (nameLower.Contains(kvp.Key.ToLowerInvariant()))
                    return kvp.Value;
            }

            // Keyword-based detection (highest priority — overrides extension)
            if (GameKeywords.Any(kw => nameLower.Contains(kw)))
                return "Games";

            if (VideoKeywords.Any(kw => nameLower.Contains(kw)))
                return "Videos";

            if (SoftwareKeywords.Any(kw => nameLower.Contains(kw)))
                return "Software";

            // Multi-part archive with large size → likely Game
            if (Regex.IsMatch(nameLower, @"\.part\d+\.rar$"))
                return "Games"; // Multi-part RARs are almost always games

            // Extension-based fallback
            foreach (var kvp in ExtensionMap)
            {
                if (kvp.Value.Contains(ext))
                    return kvp.Key;
            }

            return "Other";
        }

        /// <summary>
        /// Get the optimal segment count based on historical data.
        /// Learns from past download performance.
        /// </summary>
        public static int GetOptimalSegments(long fileSize, string serverDomain, int defaultSegments)
        {
            var domainRecords = _profile.History
                .Where(r => r.ServerDomain == serverDomain && r.FileSize > 0)
                .OrderByDescending(r => r.Timestamp)
                .Take(20)
                .ToList();

            if (domainRecords.Count < 3)
                return defaultSegments; // Not enough data, use default

            // Group by segment count and find which gave best speed
            var bestConfig = domainRecords
                .GroupBy(r => r.SegmentsUsed)
                .Select(g => new
                {
                    Segments = g.Key,
                    AvgSpeed = g.Average(r => r.AverageSpeedMBps),
                    Count = g.Count()
                })
                .Where(g => g.Count >= 2) // Need at least 2 samples
                .OrderByDescending(g => g.AvgSpeed)
                .FirstOrDefault();

            if (bestConfig != null)
                return bestConfig.Segments;

            // Size-based heuristic fallback
            return fileSize switch
            {
                < 50 * 1024 * 1024 => 1,            // < 50MB: single stream
                < 500 * 1024 * 1024 => 4,            // < 500MB: 4 segments
                < 2L * 1024 * 1024 * 1024 => 8,      // < 2GB: 8 segments
                _ => 16                               // 2GB+: 16 segments
            };
        }

        /// <summary>
        /// Log a completed download for future optimization.
        /// </summary>
        public static void LogDownload(DownloadRecord record)
        {
            record.Timestamp = DateTime.UtcNow;
            _profile.History.Add(record);

            // Keep only last 200 records to save space
            if (_profile.History.Count > 200)
                _profile.History = _profile.History.OrderByDescending(r => r.Timestamp).Take(200).ToList();

            Save();
        }

        /// <summary>
        /// Get summary stats for display in the UI.
        /// </summary>
        public static (int TotalDownloads, double TotalGB, string TopCategory) GetStats()
        {
            var total = _profile.History.Count;
            var totalGB = _profile.History.Sum(r => r.FileSize) / (1024.0 * 1024 * 1024);
            var topCat = _profile.History
                .GroupBy(r => r.Category)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key ?? "—";
            return (total, totalGB, topCat);
        }

        /// <summary>
        /// Let user correct a categorization — this is "learning".
        /// </summary>
        public static void LearnCategoryOverride(string keyword, string category)
        {
            _profile.UserCategoryOverrides[keyword.ToLowerInvariant()] = category;
            Save();
        }

        #region Persistence
        private static void Load()
        {
            try
            {
                if (File.Exists(ProfilePath))
                {
                    var json = File.ReadAllText(ProfilePath);
                    _profile = JsonConvert.DeserializeObject<SmartProfile>(json) ?? new SmartProfile();
                }
            }
            catch { _profile = new SmartProfile(); }
        }

        private static void Save()
        {
            try
            {
                File.WriteAllText(ProfilePath, JsonConvert.SerializeObject(_profile, Formatting.Indented));
            }
            catch { }
        }
        #endregion
    }
}
