using System;
using System.IO;

namespace LGNetworkCast.Core
{
    public static class Log
    {
        static readonly object Gate = new object();
        public static string FilePath => Path.Combine(Settings.Folder, "log.txt");

        public static void Info(string message) => Write("INFO", message);
        public static void Error(string message, Exception ex = null) => Write("ERROR", ex == null ? message : $"{message}: {ex}");

        static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Settings.Folder);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Logging must never break the app.
            }
        }
    }
}
