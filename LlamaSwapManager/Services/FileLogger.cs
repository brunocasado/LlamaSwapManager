using System;
using System.IO;
using System.Linq;

namespace LlamaSwapManager.Services;

/// <summary>
/// Singleton file logger — writes timestamped log lines to a daily-rotating file.
/// Thread-safe, append-only, no external dependencies.
/// </summary>
public class FileLogger
{
    private const string LogDirName = "logs";
    private const string LogFileNamePattern = "llama-swap-manager-{0:yyyy-MM-dd}.log";

    private static readonly Lazy<FileLogger> _instance = new(() => new FileLogger());
    private static readonly object _lock = new();
    private StreamWriter? _writer;
    private DateTime _logDate;

    public static FileLogger Instance => _instance.Value;

    /// <summary>
    /// Maximum number of log files to keep. Oldest files are deleted on rotation.
    /// </summary>
    public int MaxLogFiles { get; set; } = 30;

    private FileLogger()
    {
        _logDate = DateTime.Now.Date;
        OpenLogFile();
    }

    /// <summary>
    /// Write a log message with the current timestamp.
    /// </summary>
    public void Log(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var line = $"[{timestamp}] {message}";

        lock (_lock)
        {
            // Rotate if date changed
            if (DateTime.Now.Date != _logDate)
            {
                Rotate();
            }

            // Ensure writer is open
            if (_writer == null || _writer.BaseStream == null)
            {
                OpenLogFile();
            }

            try
            {
                _writer?.WriteLine(line);
                _writer?.Flush();
            }
            catch
            {
                // Best-effort logging — don't crash the app
            }
        }
    }

    /// <summary>
    /// Explicitly close and reopen the log file (useful for manual rotation).
    /// </summary>
    public void Rotate()
    {
        lock (_lock)
        {
            CloseWriter();
            OpenLogFile();
            CleanupOldLogs();
        }
    }

    private void OpenLogFile()
    {
        try
        {
            var logDir = GetLogDirectory();
            Directory.CreateDirectory(logDir);

            var fileName = string.Format(LogFileNamePattern, DateTime.Now);
            var filePath = Path.Combine(logDir, fileName);

            _writer = new StreamWriter(File.Open(filePath, FileMode.Append, FileAccess.Write, FileShare.Read), System.Text.Encoding.UTF8)
            {
                AutoFlush = true
            };
            _logDate = DateTime.Now.Date;
        }
        catch
        {
            // If we can't open the log file, silently fail
            // The app should still work
        }
    }

    private void CloseWriter()
    {
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
        catch { }
    }

    private string GetLogDirectory()
    {
        // Logs go to the user's AppData/Roaming directory on Windows,
        // ~/Library/Application Support on macOS, or ~/.local/share on Linux.
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LlamaSwapManager");
        return Path.Combine(baseDir, LogDirName);
    }

    private void CleanupOldLogs()
    {
        try
        {
            var logDir = GetLogDirectory();
            if (!Directory.Exists(logDir)) return;

            var logFiles = Directory.GetFiles(logDir, "*.log")
                .OrderByDescending(f => f) // Newest first (filename contains date)
                .ToList();

            while (logFiles.Count > MaxLogFiles)
            {
                var oldest = logFiles[logFiles.Count - 1];
                File.Delete(oldest);
                logFiles.RemoveAt(logFiles.Count - 1);
            }
        }
        catch { }
    }

    /// <summary>
    /// Get the path to the log directory (useful for opening in file manager).
    /// </summary>
    public static string GetLogDirectoryPath()
    {
        return _instance.Value.GetLogDirectory();
    }

    /// <summary>
    /// Get the path to today's log file.
    /// </summary>
    public static string GetTodayLogFilePath()
    {
        var logDir = GetLogDirectoryPath();
        var fileName = string.Format(LogFileNamePattern, DateTime.Now);
        return Path.Combine(logDir, fileName);
    }
}
