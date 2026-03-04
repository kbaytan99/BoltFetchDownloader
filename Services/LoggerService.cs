using System;
using System.IO;
using System.Diagnostics;

namespace BoltFetch.Services
{
    public static class Logger
    {
        private static readonly string LogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        private static readonly object _lock = new object();

        static Logger()
        {
            if (!Directory.Exists(LogDir))
            {
                try { Directory.CreateDirectory(LogDir); } catch { }
            }
        }

        private static string GetLogFilePath()
        {
            string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
            return Path.Combine(LogDir, $"BoltFetch_{dateStr}.log");
        }

        public static void Info(string message)
        {
            WriteLog("INFO", message);
        }

        public static void Warn(string message)
        {
            WriteLog("WARN", message);
        }

        public static void Error(string message, Exception? ex = null)
        {
            if (ex != null)
                WriteLog("ERROR", $"{message}\nException: {ex.Message}\nStackTrace: {ex.StackTrace}");
            else
                WriteLog("ERROR", message);
        }

        private static void WriteLog(string level, string message)
        {
            try
            {
                lock (_lock)
                {
                    string time = DateTime.Now.ToString("HH:mm:ss.fff");
                    string logEntry = $"[{time}] [{level}] {message}{Environment.NewLine}";
                    File.AppendAllText(GetLogFilePath(), logEntry);
                    Debug.WriteLine(logEntry.TrimEnd());
                }
            }
            catch
            {
                // Can't write log, fail silently to avoid crashing the app
            }
        }
    }
}
