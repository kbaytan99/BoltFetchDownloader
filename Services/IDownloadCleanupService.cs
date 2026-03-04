namespace BoltFetch.Services;

public interface IDownloadCleanupService
{
    void DeleteDownloadFiles(string downloadPath, string fileName);
}
