using System;
using System.Windows;
using System.Diagnostics;
using System.Web;
using System.Reflection;

namespace BoltFetch
{
    public partial class GlobalErrorWindow : Window
    {
        private string _fullError;
        private string _contextInfo = string.Empty;

        public GlobalErrorWindow(string errorMessage, string context = null)
        {
            InitializeComponent();
            _fullError = errorMessage;
            ErrorMessageText.Text = errorMessage;
            
            if (!string.IsNullOrEmpty(context))
            {
                _contextInfo = context;
                ContextMessageText.Text = context;
                ContextBorder.Visibility = Visibility.Visible;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void ReportBug_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Capture Screenshot and save to clipboard
                CaptureScreenshotToClipboard();

                // Build rich context metadata
                string osInfo = Environment.OSVersion.VersionString;
                string appVersion = "Unknown";
                try 
                {
                    appVersion = System.Reflection.Assembly.GetExecutingAssembly()
                        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
                    if (appVersion.Contains('+')) appVersion = appVersion[..appVersion.IndexOf('+')];
                } catch { }

                string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                
                string contextBlock = string.IsNullOrEmpty(_contextInfo) ? "" : $"\n\n### Additional Context:\n```\n{_contextInfo}\n```";

                // Format the error into the GitHub issue URL using query string
                string issueTitle = Uri.EscapeDataString("Auto-Crash Report");
                string issueBody = Uri.EscapeDataString($"I experienced an unexpected error.\n\n### Environment:\n- **Time**: {timeStr}\n- **OS**: {osInfo}\n- **App Version**: {appVersion}\n\n### Error Details:\n```\n{_fullError}\n```{contextBlock}\n\n### Steps to reproduce:\n1.\n\n*(Note: An screenshot of the app was automatically copied to your clipboard. Just press **Ctrl + V** below to attach it!)*");
                
                string githubUrl = $"https://github.com/kbaytan99/BoltFetch-Downloader/issues/new?title={issueTitle}&body={issueBody}";

                Process.Start(new ProcessStartInfo
                {
                    FileName = githubUrl,
                    UseShellExecute = true
                });

                ClipboardHint.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to open browser. {ex.Message}");
            }
            
            // We do not close the window immediately so user can see the "Screenshot copied!" hint.
        }

        private void CaptureScreenshotToClipboard()
        {
            try
            {
                var mainWindow = System.Windows.Application.Current.MainWindow;
                if (mainWindow != null && mainWindow.IsVisible)
                {
                    double w = mainWindow.ActualWidth;
                    double h = mainWindow.ActualHeight;

                    // If minimized or invalid, skip
                    if (w <= 0 || h <= 0) return;

                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)w, (int)h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        
                    // Make sure it is rendered with white background (to avoid transparent blacks)
                    var drawingVisual = new System.Windows.Media.DrawingVisual();
                    using (var drawingContext = drawingVisual.RenderOpen())
                    {
                        var visualBrush = new System.Windows.Media.VisualBrush(mainWindow);
                        drawingContext.DrawRectangle(
                            System.Windows.Media.Brushes.Black, 
                            null, 
                            new System.Windows.Rect(new System.Windows.Point(0, 0), new System.Windows.Point(w, h)));
                        drawingContext.DrawRectangle(
                            visualBrush, 
                            null, 
                            new System.Windows.Rect(new System.Windows.Point(0, 0), new System.Windows.Point(w, h)));
                    }
                    
                    rtb.Render(drawingVisual);
                    
                    System.Windows.Clipboard.SetImage(rtb);
                }
            }
            catch { /* Ignore screenshot errors so it doesn't break the bug reporter */ }
        }

        private void Window_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                this.DragMove();
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // Close the window if the user clicks out
            // this.Close(); 
            // Better to keep it open so user can report the bug. We will just comment it out.
        }
    }
}
