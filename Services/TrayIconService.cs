using System;
using System.Windows;
using Forms = System.Windows.Forms;

namespace BoltFetch.Services
{
    public interface ITrayIconService
    {
        void Initialize(Window mainWindow, Action onSettingsClicked, Action onAboutClicked);
        void HideToTray();
        void Dispose();
    }

    public class TrayIconService : ITrayIconService
    {
        private Forms.NotifyIcon? _notifyIcon;
        private Window? _mainWindow;

        public void Initialize(Window mainWindow, Action onSettingsClicked, Action onAboutClicked)
        {
            _mainWindow = mainWindow;
            
            try
            {
                _notifyIcon = new Forms.NotifyIcon();
                
                System.Drawing.Icon appIcon = System.Drawing.SystemIcons.Information;
                try 
                {
                    var streamInfo = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Resources/logo.png"));
                    if (streamInfo != null)
                    {
                        appIcon = GetTrayIcon(streamInfo.Stream);
                        // Taskbar icon mapping for WPF
                        var wpfIcon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                            appIcon.Handle,
                            Int32Rect.Empty,
                            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                        _mainWindow.Icon = wpfIcon;
                    }
                }
                catch { }

                _notifyIcon.Icon = appIcon;
                _notifyIcon.Visible = true;
                _notifyIcon.Text = "BoltFetch Downloader";
                _notifyIcon.DoubleClick += (s, e) => RestoreFromTray();

                var contextMenu = new Forms.ContextMenuStrip();
                contextMenu.Items.Add("Settings", null, (s, e) => _mainWindow.Dispatcher.Invoke(onSettingsClicked));
                contextMenu.Items.Add("About", null, (s, e) => _mainWindow.Dispatcher.Invoke(onAboutClicked));
                contextMenu.Items.Add("-");
                contextMenu.Items.Add("Restore", null, (s, e) => RestoreFromTray());
                contextMenu.Items.Add("-");
                contextMenu.Items.Add("Exit", null, (s, e) => System.Windows.Application.Current.Shutdown());
                _notifyIcon.ContextMenuStrip = contextMenu;
            }
            catch (Exception ex) 
            {
                // Simple fallback log or ignore if notify icon fails
            }
        }

        public void HideToTray()
        {
            _mainWindow?.Dispatcher.BeginInvoke(new Action(() => 
            {
                _mainWindow.Hide();
            }));
        }

        private void RestoreFromTray()
        {
            if (_mainWindow != null)
            {
                _mainWindow.Show();
                _mainWindow.WindowState = WindowState.Normal;
                _mainWindow.Activate();
            }
        }

        public void Dispose()
        {
            _notifyIcon?.Dispose();
        }

        private System.Drawing.Icon GetTrayIcon(System.IO.Stream stream)
        {
            using (var bmp = new System.Drawing.Bitmap(stream))
            {
                int left = bmp.Width, top = bmp.Height, right = 0, bottom = 0;
                for (int y = 0; y < bmp.Height; y += 2) 
                {
                    for (int x = 0; x < bmp.Width; x += 2)
                    {
                        if (bmp.GetPixel(x, y).A > 10)
                        {
                            if (x < left) left = x;
                            if (x > right) right = x;
                            if (y < top) top = y;
                            if (y > bottom) bottom = y;
                        }
                    }
                }

                int w = right - left + 1;
                int h = bottom - top + 1;
                if (w <= 0 || h <= 0) { w = bmp.Width; h = bmp.Height; left = 0; top = 0; }

                int size = Math.Max(w, h);
                var squareBmp = new System.Drawing.Bitmap(size, size);
                using (var g = System.Drawing.Graphics.FromImage(squareBmp))
                {
                    int drawX = (size - w) / 2;
                    int drawY = (size - h) / 2;
                    g.DrawImage(bmp, new System.Drawing.Rectangle(drawX, drawY, w, h), new System.Drawing.Rectangle(left, top, w, h), System.Drawing.GraphicsUnit.Pixel);
                }

                var finalBmp = new System.Drawing.Bitmap(128, 128);
                using (var g = System.Drawing.Graphics.FromImage(finalBmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(squareBmp, 0, 0, 128, 128);
                }

                return System.Drawing.Icon.FromHandle(finalBmp.GetHicon());
            }
        }
    }
}
