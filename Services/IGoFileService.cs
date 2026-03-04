using System.Collections.Generic;
using System.Threading.Tasks;
using BoltFetch.Models;

namespace BoltFetch.Services
{
    public interface IGoFileService : IDownloadProvider
    {
        Task InitializeAsync();
        Task<List<GoFileItem>> GetFolderContents(string folderCode);
    }
}
