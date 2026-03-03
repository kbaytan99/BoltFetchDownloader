using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace BoltFetch.Services
{
    public class ClipboardMonitor
    {
        private readonly DispatcherTimer _timer;
        private string _lastCapturedText = string.Empty;
        public event Action<string[]>? LinksDetected;

        public ClipboardMonitor()
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _timer.Tick += Timer_Tick;
        }

        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();

        private void Timer_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    string text = System.Windows.Clipboard.GetText().Trim();
                    if (string.IsNullOrEmpty(text) || text == _lastCapturedText) return;

                    var matches = Regex.Matches(text, @"(?:https?://)?(?:www\.)?gofile\.io/d/[a-zA-Z0-9\-]+", RegexOptions.IgnoreCase);
                    if (matches.Count > 0)
                    {
                        _lastCapturedText = text;
                        var links = new string[matches.Count];
                        for (int i = 0; i < matches.Count; i++) links[i] = matches[i].Value;
                        LinksDetected?.Invoke(links);
                    }
                }
            }
            catch { /* Clipboard occupied by another process */ }
        }
    }
}
