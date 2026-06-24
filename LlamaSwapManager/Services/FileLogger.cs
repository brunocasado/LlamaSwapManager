using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

/// <summary>
/// Async singleton file logger — enqueues messages to a background task that
/// writes in batches. Non-blocking on the caller thread.
/// Thread-safe, append-only, no external dependencies.
/// </summary>
public class FileLogger : IDisposable
{
    private const string LogDirName = "logs";
    private const string LogFileNamePattern = "llama-swap-manager-{0:yyyy-MM-dd}.log";

    /// <summary>
    /// Maximum messages queued before dropping oldest.
    /// </summary>
    private const int QueueCapacity = 1000;

    /// <summary>
    /// Flush interval — disk write happens at least every 500ms.
    /// </summary>
    private const int FlushIntervalMs = 500;

    /// <summary>
    /// Batch size — disk write happens when 50 lines accumulate.
    /// </summary>
    private const int BatchSize = 50;

    private static readonly Lazy<FileLogger> _instance = new(() => new FileLogger());

    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _drainTask;
    private readonly object _lock = new();

    private StreamWriter? _writer;
    private DateTime _logDate;
    private bool _disposed;

    public static FileLogger Instance => _instance.Value;

    /// <summary>
    /// Maximum number of log files to keep. Oldest files are deleted on rotation.
    /// </summary>
    public int MaxLogFiles { get; set; } = 30;

    private FileLogger()
    {
        _logDate = DateTime.Now.Date;
        _drainTask = Task.Run(DrainAsync, _cts.Token);
    }

    /// <summary>
    /// Enqueue a log message — returns immediately without blocking.
    /// </summary>
    public void Log(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || _disposed) return;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var line = $"[{timestamp}] {message}";

        // Non-blocking — drops oldest if queue is full
        _queue.Writer.TryWrite(line);
    }

    /// <summary>
    /// Background task: drain the channel and write to disk in batches.
    /// </summary>
    private async Task DrainAsync()
    {
        var batch = new List<string>(BatchSize);
        var flushInterval = TimeSpan.FromMilliseconds(FlushIntervalMs);
        var lastFlush = DateTime.UtcNow;

        await foreach (var line in _queue.Reader.ReadAllAsync(_cts.Token))
        {
            batch.Add(line);

            var now = DateTime.UtcNow;
            if (batch.Count >= BatchSize || (now - lastFlush) >= flushInterval)
            {
                FlushBatch(batch);
                lastFlush = now;
            }
        }

        // Drain remaining messages after channel is closed
        if (batch.Count > 0)
        {
            FlushBatch(batch);
        }
    }

    private void FlushBatch(List<string> batch)
    {
        lock (_lock)
        {
            // Rotate if date changed
            if (DateTime.Now.Date != _logDate)
            {
                RotateInternal();
            }

            // Ensure writer is open
            if (_writer == null || _writer.BaseStream == null)
            {
                OpenLogFile();
            }

            try
            {
                foreach (var line in batch)
                {
                    _writer?.WriteLine(line);
                }
                _writer?.Flush();
            }
            catch
            {
                // Best-effort logging — don't crash the app
            }
            finally
            {
                batch.Clear();
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
            RotateInternal();
        }
    }

    private void RotateInternal()
    {
        CloseWriter();
        OpenLogFile();
        CleanupOldLogs();
    }

    private void OpenLogFile()
    {
        try
        {
            var logDir = GetLogDirectory();
            Directory.CreateDirectory(logDir);

            var fileName = string.Format(LogFileNamePattern, DateTime.Now);
            var filePath = Path.Combine(logDir, fileName);

            _writer = new StreamWriter(
                File.Open(filePath, FileMode.Append, FileAccess.Write, FileShare.Read),
                System.Text.Encoding.UTF8)
            {
                AutoFlush = false  // We control flushing via batches
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
        var logDir = Path.Combine(baseDir, LogDirName);

        // Set restrictive permissions on log directory (owner-only access on Unix)
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"700 \"{logDir}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(2000);
            }
            catch
            {
                // Non-fatal — logs will still work, just with default permissions
            }
        }

        return logDir;
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

    /// <summary>
    /// Gracefully shut down: close the channel, drain remaining messages, dispose resources.
    /// Call on application exit.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Signal the drain task to finish
        _queue.Writer.Complete();

        // Wait up to 2 seconds for remaining messages to flush
        try
        {
            _drainTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore — app is shutting down
        }

        // Close file handles
        lock (_lock)
        {
            CloseWriter();
        }

        _cts.Dispose();
    }
}
