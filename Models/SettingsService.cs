using System;
using System.IO;
using Newtonsoft.Json;

namespace BoltFetch.Models
{
    public class UserSettings
    {
        public string DownloadPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "BoltFetch");
        public int SpeedLimitKB { get; set; } = 0; // 0 = No limit
        public int MaxParallelDownloads { get; set; } = 3;
        public int SegmentsPerFile { get; set; } = 4;
    }

    public static class SettingsService
    {
        private static readonly string SettingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        public static UserSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    return JsonConvert.DeserializeObject<UserSettings>(json) ?? new UserSettings();
                }
            }
            catch { }
            return new UserSettings();
        }

        public static void Save(UserSettings settings)
        {
            try
            {
                var directory = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                
                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(SettingsFilePath, json);
            }
            catch { }
        }
    }
}
