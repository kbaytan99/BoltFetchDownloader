using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BoltFetch.Models
{
    public class GoFileService : IDownloadProvider
    {
        public string Name => "GoFile";
        
        public bool CanHandle(string url) => url.Contains("gofile.io/d/");

        public async Task<List<GoFileItem>> FetchFilesAsync(string url)
        {
            var match = Regex.Match(url, @"/d/([a-zA-Z0-9]+)");
            if (match.Success)
            {
                return await GetFolderContents(match.Groups[1].Value);
            }
            return new List<GoFileItem>();
        }
        private static readonly HttpClient _httpClient = new HttpClient();
        private string _websiteToken = "";
        private string _guestToken = "";

        static GoFileService()
        {
            // Consistent headers mimicking a browser
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Origin", "https://gofile.io");
            _httpClient.DefaultRequestHeaders.Add("Referer", "https://gofile.io/");
        }

        public async Task InitializeAsync()
        {
            int maxRetries = 2;
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    await FetchWebsiteToken();
                    await FetchGuestToken();
                    return;
                }
                catch
                {
                    if (i == maxRetries - 1) throw;
                    await Task.Delay(1500); // Wait on failure
                }
            }
        }

        private async Task FetchWebsiteToken()
        {
            try
            {
                var content = await _httpClient.GetStringAsync("https://gofile.io/dist/js/config.js");
                var match = Regex.Match(content, "appdata\\.wt\\s*=\\s*\"([^\"]+)\"");
                if (match.Success)
                {
                    _websiteToken = match.Groups[1].Value;
                }
                else
                {
                    _websiteToken = "4fd6sg89d7s6";
                }
            }
            catch
            {
                _websiteToken = "4fd6sg89d7s6";
            }
        }

        private async Task FetchGuestToken()
        {
            try
            {
                var response = await _httpClient.PostAsync("https://api.gofile.io/accounts", null);
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync();
                var json = JsonNode.Parse(content);
                _guestToken = json?["data"]?["token"]?.ToString() ?? string.Empty;

                if (string.IsNullOrEmpty(_guestToken))
                {
                    throw new Exception("GoFile API returned an empty token.");
                }

                var validateRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.gofile.io/accounts/website");
                validateRequest.Headers.Add("Authorization", $"Bearer {_guestToken}");
                var validateResponse = await _httpClient.SendAsync(validateRequest);
                validateResponse.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to create or validate guest session on GoFile. ({ex.Message})", ex);
            }
        }

        public async Task<List<GoFileItem>> GetFolderContents(string folderCode)
        {
            int maxRetries = 5;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    if (string.IsNullOrEmpty(_guestToken)) await InitializeAsync();

                    var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.gofile.io/contents/{folderCode}?contentFilter=&page=1&pageSize=1000&sortField=name&sortDirection=1");
                    request.Headers.Add("Authorization", $"Bearer {_guestToken}");
                    request.Headers.Add("X-Website-Token", _websiteToken);

                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    BoltFetch.Services.Logger.Info($"Targeting GoFile folder '{folderCode}', attempt {attempt}...");

                    var response = await _httpClient.SendAsync(request);
                    
                    if ((int)response.StatusCode == 429)
                    {
                        BoltFetch.Services.Logger.Warn($"GoFile rate limit (429) reached for folder '{folderCode}'. Waiting before retry...");
                        if (attempt < maxRetries) 
                        {
                            await Task.Delay(3000 * attempt);
                            continue;
                        }
                    }

                    if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                    {
                        BoltFetch.Services.Logger.Warn($"GoFile token expired for folder '{folderCode}', status: {response.StatusCode}.");
                        _guestToken = "";
                        _websiteToken = "";
                        if (attempt < maxRetries) continue;
                    }

                    response.EnsureSuccessStatusCode();

                    var content = await response.Content.ReadAsStringAsync();
                    stopwatch.Stop();
                    BoltFetch.Services.Logger.Info($"GoFile folder '{folderCode}' returned in {stopwatch.ElapsedMilliseconds} ms. Response length: {content.Length}");

                    var json = JsonNode.Parse(content);
                    
                    if (json?["status"]?.ToString() != "ok")
                    {
                        if (json?["status"]?.ToString() == "error-auth" && attempt < maxRetries)
                        {
                            BoltFetch.Services.Logger.Warn($"GoFile error-auth status for folder '{folderCode}'.");
                            _guestToken = "";
                            _websiteToken = "";
                            continue;
                        }
                        BoltFetch.Services.Logger.Error($"GoFile API returned error status: {json?["status"]?.ToString()}");
                        throw new Exception($"API Error: {json?["status"]?.ToString()}");
                    }
                    
                    var items = new List<GoFileItem>();
                    var children = json?["data"]?["children"]?.AsObject();

                    if (children != null)
                    {
                        foreach (var childNode in children)
                        {
                            var child = childNode.Value;
                            if (child?["type"]?.ToString() == "file")
                            {
                                items.Add(new GoFileItem
                                {
                                    Id = child?["id"]?.ToString() ?? string.Empty,
                                    Name = child?["name"]?.ToString() ?? string.Empty,
                                    Size = child?["size"]?.GetValue<long>() ?? 0,
                                    DownloadLink = child?["link"]?.ToString() ?? string.Empty,
                                    Md5 = child?["md5"]?.ToString() ?? string.Empty,
                                    Token = _guestToken
                                });
                            }
                        }
                    }

                    return items;
                }
                catch (Exception ex)
                {
                    if (attempt == maxRetries)
                    {
                        throw new Exception($"GoFile retrieval failed after {maxRetries} attempts: {ex.Message}");
                    }
                    await Task.Delay(1500 * attempt);
                }
            }
            return new List<GoFileItem>();
        }
    }

    public class GoFileItem
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public long Size { get; set; }
        public string DownloadLink { get; set; } = string.Empty;
        public string Md5 { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;

        public string SizeFormatted
        {
            get
            {
                string[] units = { "B", "KB", "MB", "GB", "TB" };
                double size = Size;
                int unitIndex = 0;
                while (size >= 1024 && unitIndex < units.Length - 1)
                {
                    size /= 1024;
                    unitIndex++;
                }
                return $"{size:F2} {units[unitIndex]}";
            }
        }
    }
}
