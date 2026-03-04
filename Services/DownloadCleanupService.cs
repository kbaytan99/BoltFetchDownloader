using System;
using System.IO;

namespace BoltFetch.Services;

public class DownloadCleanupService : IDownloadCleanupService
{
    public void DeleteDownloadFiles(string downloadPath, string fileName)
    {
        try
        {
            var filePath = Path.Combine(downloadPath, fileName);
            
            // 1. Delete main file if exists
            if (File.Exists(filePath)) File.Delete(filePath);
            
            // 2. Delete temporary artifacts
            string[] extensions = { ".downloading", ".downloading.state", ".state" };
            foreach (var ext in extensions)
            {
                var p = filePath + ext;
                if (File.Exists(p)) File.Delete(p);
            }
            
            // 3. Clean up fragments (.part1, .part2, etc.)
            // We scan up to 128 fragments (well above default limits)
            for (int i = 1; i <= 128; i++)
            {
                var partPath = filePath + ".part" + i;
                if (File.Exists(partPath)) File.Delete(partPath);
            }
            
            Logger.Info($"DownloadCleanupService: Cleaned up files for '{fileName}' in {downloadPath}");
        }
        catch (Exception ex)
        {
            Logger.Error($"DownloadCleanupService: Error deleting files for '{fileName}': {ex.Message}");
            throw; // Re-throw to let the caller decide how to inform the user
        }
    }
}
