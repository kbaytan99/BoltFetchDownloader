using System.Collections.Generic;
using System.Threading.Tasks;

namespace BoltFetch.Models
{
    public interface IDownloadProvider
    {
        string Name { get; }
        bool CanHandle(string url);
        Task<List<GoFileItem>> FetchFilesAsync(string url);
    }
}
