using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
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

public class LlamaSwapProcessManager : IDisposable
{
    private const int SigTerm = 15;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private Process? _process;
    private readonly object _lock = new();

    public event Action<LlamaSwapStatus>? StatusChanged;
    public event Action<string>? LogMessage;

    public LlamaSwapStatus Status { get; private set; } = LlamaSwapStatus.Stopped;
    public string? ExecutablePath { get; private set; }
    public string? ConfigPath { get; private set; }
    public string? ApiBaseUrl { get; private set; }
    public string? LlamaSwapExePath => ExecutablePath;
    public string? DetectedApiBaseUrl => ApiBaseUrl;
    public string AppDirectory { get; }
    public string UserDirectory { get; }
    public string? WorkingDirectory { get; private set; }

    public LlamaSwapProcessManager(string? appDirectory = null, string? userDirectory = null)
    {
        AppDirectory = appDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
        UserDirectory = userDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llama-swap");
    }

    public void ResolvePaths()
    {
        ExecutablePath = ResolveExecutable();
        ConfigPath = ResolveConfig();

        if (ExecutablePath is not null)
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath);
        else if (ConfigPath is not null)
            WorkingDirectory = Path.GetDirectoryName(ConfigPath);
        else
            WorkingDirectory = AppDirectory;

    }

    public void RefreshPaths() => ResolvePaths();
    public void DetectApiUrl() => ApiBaseUrl = DetectApiBaseUrl();

    private string? ResolveExecutable()
    {
        var exeSuffix = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
        var candidates = new[]
        {
            Path.Combine(AppDirectory, "llama-swap" + exeSuffix),
            Path.Combine(UserDirectory, "llama-swap" + exeSuffix)
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var fullPath = Path.Combine(dir, "llama-swap" + exeSuffix);
            if (File.Exists(fullPath))
                return fullPath;
        }

        return null;
    }

    private string? ResolveConfig()
    {
        var candidates = new[]
        {
            // Prefer the real user config. The build output may contain a stale
            // bundled config.yml; using it makes edits appear in preview but vanish
            // after restart because llama-swap/user config remains unchanged.
            Path.Combine(UserDirectory, "config.yml"),
            Path.Combine(AppDirectory, "config.yml")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

   public async Task<bool> StartAsync()
    {
        ResolvePaths();
        LogProcessSnapshot("start requested");
        var existingApi = DetectApiBaseUrl();
        if (Status == LlamaSwapStatus.Running || existingApi is not null)
        {
            ApiBaseUrl = existingApi;
            SetStatus(LlamaSwapStatus.Running);
            LogMessage?.Invoke($"[manager] llama-swap already running (API: {ApiBaseUrl})");
            return true;
        }

        if (ExecutablePath is null)
        {
            LogMessage?.Invoke("llama-swap executable not found");
            return false;
        }

        if (ConfigPath is null)
        {
            LogMessage?.Invoke("config.yml not found");
            return false;
        }

        SetStatus(LlamaSwapStatus.Starting);
        LogMessage?.Invoke($"[manager] starting llama-swap: exe={ExecutablePath} config={ConfigPath}");

        try
        {
            var args = $"--config \"{ConfigPath}\"";

            // Unblock file on Windows
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    var psiUnblock = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"Unblock-File -Path \"{ExecutablePath}\"",
                        UseShellExecute = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(psiUnblock)?.WaitForExit();
                }
                catch (Exception ex) { LogMessage?.Invoke($"[manager] unload failed: {ex.Message}"); }
            }

            var psiStart = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                Arguments = args,
                WorkingDirectory = WorkingDirectory ?? AppDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _process = new Process { StartInfo = psiStart, EnableRaisingEvents = true };
            _process.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) LogMessage?.Invoke($"[out] {e.Data}"); };
            _process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) LogMessage?.Invoke($"[err] {e.Data}"); };
            _process.Exited += OnProcessExited;

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            var apiReady = await WaitForApiReadyAsync(15);

            if (apiReady)
            {
                ApiBaseUrl = DetectApiBaseUrl();
                SetStatus(LlamaSwapStatus.Running);
                LogMessage?.Invoke($"[manager] llama-swap started (API: {ApiBaseUrl})");
                return true;
            }
            else
            {
                SetStatus(LlamaSwapStatus.Error);
                LogProcessSnapshot("API not responding after start");
                LogMessage?.Invoke("[manager] llama-swap started but API not responding");
                return false;
            }
        }
        catch (Exception ex)
        {
            SetStatus(LlamaSwapStatus.Error);
            LogMessage?.Invoke($"Error starting: {ex.Message}");
            return false;
        }
    }

    public Task<bool> StartLlamaSwapAsync() => StartAsync();
    public Task<bool> StopLlamaSwapAsync() => StopAsync();
    public Task<bool> RestartLlamaSwapAsync() => RestartAsync();


    private void LogProcessSnapshot(string reason)
    {
        try
        {
            var swaps = Process.GetProcessesByName("llama-swap").Select(p => $"llama-swap:{p.Id}").ToList();
            var servers = Process.GetProcessesByName("llama-server").Select(p => $"llama-server:{p.Id}").ToList();
            var api = DetectApiBaseUrl();
            LogMessage?.Invoke($"[manager] {reason} | api={api ?? "none"} | processes={string.Join(", ", swaps.Concat(servers).DefaultIfEmpty("none"))}");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[manager] snapshot failed: {ex.Message}");
        }
    }


    private async Task<bool> GracefullyStopAllLlamaProcessesAsync(string reason, int timeoutSeconds = 3)
    {
        LogMessage?.Invoke($"[manager] gracefully stopping llama processes: {reason}");

        var processes = Process.GetProcessesByName("llama-swap")
            .Concat(Process.GetProcessesByName("llama-server"))
            .ToList();

        if (processes.Count == 0)
        {
            ApiBaseUrl = null;
            SetStatus(LlamaSwapStatus.Stopped);
            return true;
        }

        foreach (var p in processes)
        {
            try
            {
                LogMessage?.Invoke($"[manager] graceful stop pid={p.Id} name={p.ProcessName}");
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // taskkill without /F asks Windows to terminate the process tree before we escalate.
                    var psi = new ProcessStartInfo
                    {
                        FileName = "taskkill.exe",
                        Arguments = $"/T /PID {p.Id}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using var proc = Process.Start(psi);
                    if (proc is not null)
                        await proc.WaitForExitAsync();
                }
                else
                {
                    // SIGTERM gives llama-swap/llama-server a chance to clean up.
                    var rc = kill(p.Id, SigTerm);
                    if (rc != 0)
                        LogMessage?.Invoke($"[manager] SIGTERM failed pid={p.Id} errno={Marshal.GetLastWin32Error()}");
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[manager] graceful stop failed pid={p.Id}: {ex.Message}");
            }
        }

        var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
        while (DateTime.Now < deadline)
        {
            if (!IsRunning() && DetectApiBaseUrl() is null)
            {
                LogMessage?.Invoke("[manager] graceful stop completed");
                ApiBaseUrl = null;
                SetStatus(LlamaSwapStatus.Stopped);
                return true;
            }
            await Task.Delay(250);
        }

        LogProcessSnapshot("still running after graceful stop");
        return false;
    }

    private async Task<bool> KillAllLlamaProcessesAsync(string reason)
    {
        LogMessage?.Invoke($"[manager] killing llama processes: {reason}");
        var names = new[] { "llama-swap", "llama-server" };
        foreach (var name in names)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    LogMessage?.Invoke($"[manager] kill {name} pid={p.Id}");
                    p.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"[manager] kill failed {name} pid={p.Id}: {ex.Message}");
                }
            }
        }

        var deadline = DateTime.Now.AddSeconds(8);
        while (DateTime.Now < deadline)
        {
            if (!IsRunning() && DetectApiBaseUrl() is null)
            {
                LogMessage?.Invoke("[manager] all llama processes stopped");
                ApiBaseUrl = null;
                SetStatus(LlamaSwapStatus.Stopped);
                return true;
            }
            await Task.Delay(250);
        }

        LogProcessSnapshot("still running after kill");
        return !IsRunning() && DetectApiBaseUrl() is null;
    }
    public async Task<bool> StopAsync()
    {
        LogProcessSnapshot("stop requested");
        SetStatus(LlamaSwapStatus.Stopping);
        LogMessage?.Invoke("[manager] stopping llama-swap...");

        try
        {
            await TryUnloadModelAsync();
            var stopped = await GracefullyStopAllLlamaProcessesAsync("stop");
            if (!stopped)
                stopped = await KillAllLlamaProcessesAsync("stop fallback");
            SetStatus(stopped ? LlamaSwapStatus.Stopped : LlamaSwapStatus.Error);
            ApiBaseUrl = null;
            _process = null;
            LogProcessSnapshot(stopped ? "stop completed" : "stop failed");
            return stopped;
        }
        catch (Exception ex)
        {
            SetStatus(LlamaSwapStatus.Error);
            LogMessage?.Invoke($"[manager] error stopping: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> RestartAsync()
    {
        LogMessage?.Invoke("[manager] restart requested");
        LogProcessSnapshot("before restart");
        await TryUnloadModelAsync();
        var stopped = await GracefullyStopAllLlamaProcessesAsync("restart");
        if (!stopped)
            stopped = await KillAllLlamaProcessesAsync("restart fallback");
        if (!stopped)
        {
            LogMessage?.Invoke("[manager] restart aborted: could not stop existing processes");
            SetStatus(LlamaSwapStatus.Error);
            return false;
        }
        await Task.Delay(800);
        ResolvePaths();
        return await StartAsync();
    }

    public async Task<string?> GetRunningModelAsync()
    {
        var baseUrl = DetectApiBaseUrl();
        if (baseUrl is null) return null;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await http.GetAsync($"{baseUrl}/running");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var modelMatch = System.Text.RegularExpressions.Regex.Match(json, "\"model\"\\s*:\\s*\"([^\"]+)\"");
                if (modelMatch.Success)
                    return modelMatch.Groups[1].Value;
            }
        }
        catch { }

        return null;
    }

    private async Task TryUnloadModelAsync()
    {
        var baseUrl = DetectApiBaseUrl();
        if (baseUrl is null) { LogMessage?.Invoke("[manager] unload skipped: API not detected"); return; }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            LogMessage?.Invoke($"[manager] unloading model via {baseUrl}/api/models/unload");
            var response = await http.PostAsync($"{baseUrl}/api/models/unload", null);
            LogMessage?.Invoke($"[manager] unload response: {(int)response.StatusCode} {response.StatusCode}");
            if (response.IsSuccessStatusCode)
                await Task.Delay(500);
            else
                await Task.Delay(200);
        }
        catch { }
    }

    private async Task KillProcessTreeAsync()
    {
        var swapProcesses = Process.GetProcessesByName("llama-swap");
        foreach (var p in swapProcesses)
            await KillProcessTreeInternalAsync(p.Id);

        await Task.Delay(2000);

        var serverProcesses = Process.GetProcessesByName("llama-server");
        foreach (var p in serverProcesses)
            await KillProcessTreeInternalAsync(p.Id);
    }

    private Task KillProcessTreeInternalAsync(int processId)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = $"/F /T /PID {processId}",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi)?.WaitForExit();
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pkill",
                    Arguments = $"-P {processId}",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi)?.WaitForExit();

                try
                {
                    var proc = Process.GetProcessById(processId);
                    proc.Kill(true);
                }
                catch { }
            }
        }
        catch { }

        return Task.CompletedTask;
    }

    private async Task<bool> WaitForStoppedAsync(int timeoutSeconds)
    {
        var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
        while (DateTime.Now < deadline)
        {
            var swapRunning = Process.GetProcessesByName("llama-swap").Length > 0;
            var serverRunning = Process.GetProcessesByName("llama-server").Length > 0;
            if (!swapRunning && !serverRunning)
                return true;
            await Task.Delay(250);
        }
        return Process.GetProcessesByName("llama-swap").Length == 0 &&
               Process.GetProcessesByName("llama-server").Length == 0;
    }

    private async Task<bool> WaitForApiReadyAsync(int timeoutSeconds)
    {
        var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
        while (DateTime.Now < deadline)
        {
            var baseUrl = DetectApiBaseUrl();
            if (baseUrl is not null)
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                    var response = await http.GetAsync($"{baseUrl}/running");
                    if (response.IsSuccessStatusCode)
                        return true;
                }
                catch { }
            }
            await Task.Delay(500);
        }
        return false;
    }

    private async Task ForceKillAsync()
    {
        foreach (var p in Process.GetProcessesByName("llama-swap"))
            try { p.Kill(true); } catch { }
        foreach (var p in Process.GetProcessesByName("llama-server"))
            try { p.Kill(true); } catch { }
        await Task.Delay(1000);
        SetStatus(LlamaSwapStatus.Stopped);
    }

    private string? DetectApiBaseUrl()
    {
        var swapProcesses = Process.GetProcessesByName("llama-swap");
        foreach (var swap in swapProcesses)
        {
            try
            {
                var ports = GetListeningPorts(swap.Id);
                foreach (var port in ports)
                {
                    var baseUrl = $"http://127.0.0.1:{port}";
                    if (TestEndpoint(baseUrl))
                        return baseUrl;
                }
            }
            catch { }
        }

        if (TestEndpoint("http://127.0.0.1:8080"))
            return "http://127.0.0.1:8080";

        return null;
    }

    private HashSet<int> GetListeningPorts(int processId)
    {
        var ports = new HashSet<int>();

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"Get-NetTCPConnection -OwningProcess {processId} -State Listen | Select-Object -ExpandProperty LocalPort",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                var output = proc?.StandardOutput.ReadToEnd();
                if (output is not null)
                {
                    foreach (var line in output.Split('\n'))
                    {
                        if (int.TryParse(line.Trim(), out var port))
                            ports.Add(port);
                    }
                }
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "lsof",
                    Arguments = $"-iTCP -sTCP:LISTEN -p {processId} -nP",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                var output = proc?.StandardOutput.ReadToEnd();
                if (output is not null)
                {
                    foreach (var line in output.Split('\n'))
                    {
                        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var part in parts)
                        {
                            if (part.StartsWith("*:") || part.StartsWith("127.0.0.1:"))
                            {
                                var portStr = part.Split(':')[^1];
                                if (int.TryParse(portStr, out var port))
                                    ports.Add(port);
                            }
                        }
                    }
                }
            }
        }
        catch { }

        return ports;
    }

    private bool TestEndpoint(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = http.GetAsync($"{baseUrl}/running").Result;
            return response.IsSuccessStatusCode;
        }
        catch { }
        return false;
    }

    private void SetStatus(LlamaSwapStatus newStatus)
    {
        lock (_lock)
        {
            if (Status != newStatus)
            {
                Status = newStatus;
                StatusChanged?.Invoke(Status);
            }
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        SetStatus(LlamaSwapStatus.Stopped);
        ApiBaseUrl = null;
    }

    public bool IsRunning()
    {
        return Process.GetProcessesByName("llama-swap").Length > 0 ||
               Process.GetProcessesByName("llama-server").Length > 0;
    }

    public bool IsLlamaSwapProcessRunning()
    {
        return Process.GetProcessesByName("llama-swap").Length > 0;
    }

    public bool IsProxyRunning()
    {
        return Process.GetProcessesByName("llama-swap").Length > 0 || DetectApiBaseUrl() is not null;
    }

    public void Dispose() { }
}
