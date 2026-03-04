using System;
using System.IO;
using System.Text.Json;
using BoltFetch.Models;

namespace BoltFetch.Services
{
    public class SettingsService : ISettingsService
    {
        private readonly string SettingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        public UserSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var bytes = File.ReadAllBytes(SettingsFilePath);
                    return JsonSerializer.Deserialize<UserSettings>(bytes) ?? new UserSettings();
                }
            }
            catch { }
            return new UserSettings();
        }

        public void Save(UserSettings settings)
        {
            try
            {
                var directory = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                
                var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllBytes(SettingsFilePath, bytes);
            }
            catch { }
        }
    }
}
