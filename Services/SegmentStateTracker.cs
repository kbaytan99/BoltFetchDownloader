using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BoltFetch.Services
{
    public static class SegmentStateTracker
    {
        private static readonly JsonSerializerOptions _options = new JsonSerializerOptions();

        public static List<int>? LoadState(string statePath)
        {
            if (!File.Exists(statePath)) return null;
            try
            {
                var bytes = File.ReadAllBytes(statePath);
                return JsonSerializer.Deserialize<List<int>>(bytes, _options);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load state from {statePath}: {ex.Message}");
                return null;
            }
        }

        public static void SaveState(string statePath, IEnumerable<int> completedChunks)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(completedChunks, _options);
                File.WriteAllBytes(statePath, bytes);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save state to {statePath}: {ex.Message}");
            }
        }
    }
}
