using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

public enum LlamaSwapStatus
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error
}

public partial class LlamaSwapProcessManager : IDisposable
{
    private const int SigTerm = 15;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private Process? _process;
    private readonly object _lock = new();
    private readonly HttpClient _localHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    public event Action<LlamaSwapStatus>? StatusChanged;
    public event Action<string>? LogMessage;

    public LlamaSwapStatus Status { get; private set; } = LlamaSwapStatus.Stopped;
    public string? ExecutablePath { get; private set; }
    public string? ConfigPath { get; private set; }
    public string? ApiBaseUrl { get; set; }
    public string? LlamaServerBaseUrl { get; set; }
    public string? LlamaSwapExePath => ExecutablePath;
    public string? DetectedApiBaseUrl => ApiBaseUrl;
    public string? LlamaCppDirectory { get; private set; }
    public string AppDirectory { get; }
    public string UserDirectory { get; }
    public string? WorkingDirectory { get; private set; }

    private LlamaCppDownloader? _llamaCppDownloader;
    public LlamaCppDownloader GetDownloader(string? userDirectory = null)
    {
        if (_llamaCppDownloader == null)
        {
            _llamaCppDownloader = new LlamaCppDownloader(userDirectory ?? UserDirectory);
            _llamaCppDownloader.LogMessage += s => LogMessage?.Invoke(s);
        }
        return _llamaCppDownloader;
    }

    /// <summary>
    /// Tracks PIDs we own so we only kill our own processes (H3 fix).
    /// </summary>
    private readonly HashSet<int> _managedPids = new();

    public LlamaSwapProcessManager(string? appDirectory = null, string? userDirectory = null)
    {
        AppDirectory = appDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
        UserDirectory = userDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llama-swap");
    }
}
