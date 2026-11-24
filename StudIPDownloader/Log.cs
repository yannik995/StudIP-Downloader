using System;

namespace StudIPDownloader
{
    /// <summary>
    /// Thread-sichere Konsolenausgabe (Downloads laufen parallel).
    /// </summary>
    static class Log
    {
        static readonly object _lock = new object();

        public static bool Verbose { get; set; }

        public static void Info(string message)
        {
            lock (_lock)
            {
                Console.WriteLine(message);
            }
        }

        public static void Debug(string message)
        {
            if (Verbose)
            {
                Info(message);
            }
        }

        public static void Warn(string message)
        {
            Info("Warnung: " + message);
        }

        public static void Error(string message)
        {
            lock (_lock)
            {
                Console.Error.WriteLine("Fehler: " + message);
            }
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 0) return "?";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return unit == 0 ? bytes + " B" : size.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
        }
    }
}
