using System.Diagnostics;

namespace BoltFetch.Services
{
    public static class Speedometer
    {
        private static long _bytesSinceLastTick = 0;
        private static Stopwatch _sw = Stopwatch.StartNew();
        private static readonly object _lock = new object();
        private static long _lastCalculatedSpeed = 0;

        public static void AddBytes(int count)
        {
            lock (_lock)
            {
                _bytesSinceLastTick += count;
            }
        }

        public static long GetCurrentSpeed()
        {
            lock (_lock)
            {
                double elapsed = _sw.Elapsed.TotalSeconds;
                // Update speed calculation every 1 second (1.0s) for stable accurate readings
                if (elapsed >= 1.0)
                {
                    _lastCalculatedSpeed = (long)(_bytesSinceLastTick / elapsed);
                    _bytesSinceLastTick = 0;
                    _sw.Restart();
                }
                return _lastCalculatedSpeed;
            }
        }
    }
}
