using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace BoltFetch.Services
{
    public class HardwareMetrics
    {
        public double CpuUsage { get; set; }
        public double RamUsagePercentage { get; set; }
        public double RamUsedMB { get; set; }
        public double RamTotalMB { get; set; }
        public double DiskReadBytesPerSec { get; set; }
        public double DiskWriteBytesPerSec { get; set; }
        public double GpuUsage { get; set; }
        public double DiskTimeUsage { get; set; }
        public double NetworkReadBytesPerSec { get; set; }
        public double NetworkWriteBytesPerSec { get; set; }
    }

    public class HardwareMonitorService : IDisposable
    {
        private PerformanceCounter _cpuCounter;
        private PerformanceCounter _ramAvailableCounter;
        private PerformanceCounter _diskReadCounter;
        private PerformanceCounter _diskWriteCounter;
        private PerformanceCounter _diskTimeCounter;
        
        private PerformanceCounter? _gpuCategory; // Not needed if we cache counters
        private List<PerformanceCounter>? _gpuCounters;
        private DateTime _lastGpuCounterUpdate = DateTime.MinValue;
        private ulong _totalPhysicalMemory = 0;

        // Network tracking
        private long _lastNetworkReadBytes = 0;
        private long _lastNetworkWriteBytes = 0;
        private DateTime _lastNetworkUpdateTime = DateTime.MinValue;

        public HardwareMonitorService()
        {
            Task.Run(() => InitializeCounters());
        }

        private void InitializeCounters()
        {
            try
            {
                _cpuCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
                _ramAvailableCounter = new PerformanceCounter("Memory", "Available MBytes");
                _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                _diskTimeCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total");
                
                GetTotalMemory();

                if (PerformanceCounterCategory.Exists("GPU Engine"))
                {
                    var gpuCategory = new PerformanceCounterCategory("GPU Engine");
                    var instances = gpuCategory.GetInstanceNames();
                    _gpuCounters = instances
                        .Where(i => i.Contains("engtype_3D"))
                        .Select(i => new PerformanceCounter("GPU Engine", "Utilization Percentage", i))
                        .ToList();
                    foreach (var counter in _gpuCounters)
                    {
                        counter.NextValue();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error initializing hardware monitor: {ex.Message}");
            }
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        private void GetTotalMemory()
        {
            try
            {
                MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
                memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref memStatus))
                {
                    _totalPhysicalMemory = memStatus.ullTotalPhys;
                }
            }
            catch { }
        }

        public HardwareMetrics GetCurrentMetrics()
        {
            var metrics = new HardwareMetrics();

            try
            {
                if (_cpuCounter != null)
                {
                    metrics.CpuUsage = Math.Min(100, Math.Max(0, _cpuCounter.NextValue()));
                }

                if (_ramAvailableCounter != null && _totalPhysicalMemory > 0)
                {
                    double availableMB = _ramAvailableCounter.NextValue();
                    double totalMB = _totalPhysicalMemory / (1024.0 * 1024.0);
                    double usedMB = Math.Max(0, totalMB - availableMB);
                    
                    metrics.RamTotalMB = totalMB;
                    metrics.RamUsedMB = usedMB;
                    metrics.RamUsagePercentage = Math.Min(100, (usedMB / totalMB) * 100);
                }

                if (_diskReadCounter != null)
                {
                     metrics.DiskReadBytesPerSec = _diskReadCounter.NextValue();
                }

                if (_diskWriteCounter != null)
                {
                     metrics.DiskWriteBytesPerSec = _diskWriteCounter.NextValue();
                }

                if (_diskTimeCounter != null)
                {
                     metrics.DiskTimeUsage = Math.Min(100, Math.Max(0, _diskTimeCounter.NextValue()));
                }

                metrics.GpuUsage = GetGpuUsage();
                
                // Network Calculation
                long currentReadBytes = 0;
                long currentWriteBytes = 0;

                var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                             && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback);

                foreach (var ni in interfaces)
                {
                    try
                    {
                        var stats = ni.GetIPv4Statistics();
                        currentReadBytes += stats.BytesReceived;
                        currentWriteBytes += stats.BytesSent;
                    }
                    catch { } // Ignore loopback errors or lack of support
                }

                if (_lastNetworkUpdateTime != DateTime.MinValue)
                {
                    double timeScale = (DateTime.Now - _lastNetworkUpdateTime).TotalSeconds;
                    if (timeScale > 0)
                    {
                        metrics.NetworkReadBytesPerSec = Math.Max(0, (currentReadBytes - _lastNetworkReadBytes) / timeScale);
                        metrics.NetworkWriteBytesPerSec = Math.Max(0, (currentWriteBytes - _lastNetworkWriteBytes) / timeScale);
                    }
                }

                _lastNetworkReadBytes = currentReadBytes;
                _lastNetworkWriteBytes = currentWriteBytes;
                _lastNetworkUpdateTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading metrics: {ex.Message}");
            }

            return metrics;
        }

        private double GetGpuUsage()
        {
            if (_gpuCounters == null) return 0;
            
            try
            {
                // Update GPU active instances every 10 seconds to catch new games/apps
                if ((DateTime.Now - _lastGpuCounterUpdate).TotalSeconds > 10)
                {
                    _lastGpuCounterUpdate = DateTime.Now;
                    try 
                    {
                        var gpuCategory = new PerformanceCounterCategory("GPU Engine");
                        var instances = gpuCategory.GetInstanceNames();
                        var newCounters = instances
                            .Where(i => i.Contains("engtype_3D"))
                            .Select(i => new PerformanceCounter("GPU Engine", "Utilization Percentage", i))
                            .ToList();
                        
                        foreach (var counter in newCounters) counter.NextValue();

                        var oldCounters = _gpuCounters;
                        _gpuCounters = newCounters;

                        if (oldCounters != null)
                        {
                            foreach(var c in oldCounters) c.Dispose();
                        }
                    } catch { } // Ignore refresh failures
                }

                double totalUsage = 0;
                if (_gpuCounters != null)
                {
                    foreach (var counter in _gpuCounters)
                    {
                        totalUsage += counter.NextValue();
                    }
                }

                return Math.Min(100, totalUsage);
            }
            catch
            {
                return 0;
            }
        }

        public void Dispose()
        {
            _cpuCounter?.Dispose();
            _ramAvailableCounter?.Dispose();
            _diskReadCounter?.Dispose();
            _diskWriteCounter?.Dispose();
            _diskTimeCounter?.Dispose();
            if (_gpuCounters != null)
            {
                foreach(var c in _gpuCounters) c.Dispose();
            }
        }
    }
}
