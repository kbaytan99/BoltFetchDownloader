using BoltFetch.Services;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace BoltFetch
{
    public partial class HardwareMonitorWindow : Window
    {
        private HardwareMonitorService _monitorService;
        private DispatcherTimer _timer;
        private bool _isUpdating = false;

        private const int MaxHistory = 60; // 60 seconds
        private List<double> _cpuHistory = new List<double>();
        private List<double> _ramHistory = new List<double>();
        private List<double> _gpuHistory = new List<double>();
        private List<double> _diskHistory = new List<double>();
        private List<double> _netReadHistory = new List<double>();
        private List<double> _netWriteHistory = new List<double>();

        public HardwareMonitorWindow()
        {
            InitializeComponent();
            _monitorService = new HardwareMonitorService();

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;
            _timer.Start();

            // Initial manual fetch
            Timer_Tick(null, null);
        }

        private async void Timer_Tick(object sender, EventArgs e)
        {
            if (_isUpdating) return;
            _isUpdating = true;

            try
            {
                var metrics = await Task.Run(() => _monitorService.GetCurrentMetrics());

                Dispatcher.Invoke(() =>
                {
                    // Update raw text statistics
                    CpuText.Text = $"{metrics.CpuUsage:F1} %";
                    RamPercentText.Text = $"{metrics.RamUsagePercentage:F1} %";
                    RamDetailsText.Text = $"{(metrics.RamUsedMB):F0} MB / {(metrics.RamTotalMB):F0} MB";
                    GpuText.Text = $"{metrics.GpuUsage:F1} %";
                    DiskTimeText.Text = $"{metrics.DiskTimeUsage:F1} %";

                    DiskReadText.Text = FormatBytesToSize(metrics.DiskReadBytesPerSec);
                    DiskWriteText.Text = FormatBytesToSize(metrics.DiskWriteBytesPerSec);

                    NetReadText.Text = FormatBytesToSize(metrics.NetworkReadBytesPerSec);
                    NetWriteText.Text = FormatBytesToSize(metrics.NetworkWriteBytesPerSec);

                    // Add to history bounds
                    AddHistory(_cpuHistory, metrics.CpuUsage);
                    AddHistory(_ramHistory, metrics.RamUsagePercentage);
                    AddHistory(_gpuHistory, metrics.GpuUsage);
                    AddHistory(_diskHistory, metrics.DiskTimeUsage);
                    AddHistory(_netReadHistory, metrics.NetworkReadBytesPerSec);
                    AddHistory(_netWriteHistory, metrics.NetworkWriteBytesPerSec);

                    // Draw standardized % charts
                    DrawChart(CpuLine, CpuCanvas, _cpuHistory, 100);
                    DrawChart(RamLine, RamCanvas, _ramHistory, 100);
                    DrawChart(GpuLine, GpuCanvas, _gpuHistory, 100);
                    DrawChart(DiskLine, DiskCanvas, _diskHistory, 100);

                    // Draw Network (dynamic scale chart, combining both Read/Write)
                    double netMax = 1000; // Minimum 1 KB/s graph scale
                    foreach (var v in _netReadHistory) if (v > netMax) netMax = v;
                    foreach (var v in _netWriteHistory) if (v > netMax) netMax = v;
                    // Add 10% headroom
                    netMax *= 1.1; 
                    
                    DrawChart(NetReadLine, NetCanvas, _netReadHistory, netMax);
                    DrawChart(NetWriteLine, NetCanvas, _netWriteHistory, netMax);
                });
            }
            catch
            {
                // Handle failures silently
            }
            finally
            {
                _isUpdating = false;
            }
        }

        private void AddHistory(List<double> history, double val)
        {
            history.Add(val);
            if (history.Count > MaxHistory) history.RemoveAt(0);
        }

        private void DrawChart(System.Windows.Shapes.Polyline polyline, System.Windows.Controls.Canvas canvas, List<double> history, double absoluteMax)
        {
            if (history.Count == 0 || absoluteMax <= 0) 
            {
                polyline.Points.Clear();
                return;
            }

            double width = canvas.ActualWidth > 0 ? canvas.ActualWidth : 340; // Fallback width if not rendered yet
            double height = canvas.ActualHeight > 0 ? canvas.ActualHeight : 50;

            var points = new System.Windows.Media.PointCollection();
            double step = width / (MaxHistory - 1);

            for (int i = 0; i < history.Count; i++)
            {
                double x = i * step;
                
                // Calculate percentage based on max threshold
                double percentage = Math.Clamp(history[i] / absoluteMax, 0, 1);
                
                // Inverse Y (0 is top in Canvas, height is bottom)
                double y = height - (percentage * height);
                points.Add(new System.Windows.Point(x, y));
            }

            polyline.Points = points;
        }

        private string FormatBytesToSize(double bytes)
        {
            if (bytes < 1024) return $"{bytes:F0} B/s";
            if (bytes < 1024 * 1024) return $"{(bytes / 1024):F1} KB/s";
            return $"{(bytes / (1024 * 1024)):F1} MB/s";
        }

        private void Window_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                this.DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // Removed auto-close on deactivate to prevent accidental closures when dragging
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer?.Stop();
            _monitorService?.Dispose();
            base.OnClosed(e);
        }
    }
}
