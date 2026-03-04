using System.Collections.Generic;
using System.Threading.Tasks;
using BoltFetch;

namespace BoltFetch.Services;

public interface ILinkProcessingService
{
    Task<List<FileDisplayItem>> ProcessLinksAsync(List<string> urls, string downloadPath);
}
