using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BoltFetch.Models
{
    public class GoFileService
    {
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
            await FetchWebsiteToken();
            await FetchGuestToken();
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
                    // Fallback or handle error - let's try another regex if format changed
                    _websiteToken = "4fd6sg89d7s6"; // Current known token as fallback
                }
            }
            catch
            {
                _websiteToken = "4fd6sg89d7s6"; // Fallback
            }
        }

        private async Task FetchGuestToken()
        {
            try
            {
                // 1. Create guest account
                var response = await _httpClient.PostAsync("https://api.gofile.io/accounts", null);
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync();
                var json = JObject.Parse(content);
                _guestToken = json["data"]?["token"]?.ToString();

                if (string.IsNullOrEmpty(_guestToken))
                {
                    throw new Exception("GoFile API returned an empty token.");
                }

                // 2. Validate/Sync account session (Crucial for 401 errors)
                var validateRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.gofile.io/accounts/website");
                validateRequest.Headers.Add("Authorization", $"Bearer {_guestToken}");
                var validateResponse = await _httpClient.SendAsync(validateRequest);
                validateResponse.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                throw new Exception("Failed to create or validate guest session on GoFile.", ex);
            }
        }

        public async Task<List<GoFileItem>> GetFolderContents(string folderCode)
        {
            if (string.IsNullOrEmpty(_guestToken)) await InitializeAsync();

            var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.gofile.io/contents/{folderCode}?contentFilter=&page=1&pageSize=1000&sortField=name&sortDirection=1");
            request.Headers.Add("Authorization", $"Bearer {_guestToken}");
            request.Headers.Add("X-Website-Token", _websiteToken);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);
            
            var items = new List<GoFileItem>();
            var children = json["data"]?["children"];

            if (children != null)
            {
                foreach (var child in children.Values())
                {
                    if (child["type"]?.ToString() == "file")
                    {
                        items.Add(new GoFileItem
                        {
                            Id = child["id"]?.ToString(),
                            Name = child["name"]?.ToString(),
                            Size = (long?)(child["size"] ?? 0) ?? 0,
                            DownloadLink = child["link"]?.ToString(),
                            Md5 = child["md5"]?.ToString(),
                            Token = _guestToken
                        });
                    }
                }
            }

            return items;
        }
    }

    public class GoFileItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
        public string DownloadLink { get; set; }
        public string Md5 { get; set; }
        public string Token { get; set; }

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
