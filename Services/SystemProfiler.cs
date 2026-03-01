using System;

namespace BoltFetch.Services
{
    public static class SystemProfiler
    {
        public static HardwareProfile GetProfile()
        {
            var profile = new HardwareProfile
            {
                ProcessorCount = Environment.ProcessorCount,
                OSVersion = Environment.OSVersion.ToString(),
                Is64Bit = Environment.Is64BitOperatingSystem
            };

            try
            {
                var gcMemoryInfo = GC.GetGCMemoryInfo();
                profile.TotalPhysicalMemoryGB = Math.Max(1, (int)(gcMemoryInfo.TotalAvailableMemoryBytes / 1024 / 1024 / 1024));
            }
            catch
            {
                profile.TotalPhysicalMemoryGB = 8; // Fallback
            }

            return profile;
        }

        public static int GetOptimalParallelDownloads()
        {
            var profile = GetProfile();
            
            // Base logic: 1 concurrent download per 2 logical CPU cores
            int threadsBase = Math.Max(1, profile.ProcessorCount / 2);
            
            // Limit based on available memory to prevent thrashing
            if (profile.TotalPhysicalMemoryGB <= 4)
            {
                threadsBase = Math.Min(threadsBase, 2);
            }
            else if (profile.TotalPhysicalMemoryGB >= 16)
            {
                threadsBase += 2; // Extra parallel capacity for high-end PCs
            }

            // Cap at a reasonable maximum (e.g., 8) to avoid network/disk congestion
            return Math.Clamp(threadsBase, 1, 8);
        }
    }

    public class HardwareProfile
    {
        public int ProcessorCount { get; set; }
        public string OSVersion { get; set; } = string.Empty;
        public bool Is64Bit { get; set; }
        public int TotalPhysicalMemoryGB { get; set; }
    }
}
