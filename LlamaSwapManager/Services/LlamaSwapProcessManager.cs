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

        ResolveLlamaServerPath();
    }

    /// <summary>
    /// Resolves the llama.cpp binary directory by inspecting the default path
    /// and common installation locations.
    /// M4: Only trusts known directories — no arbitrary PATH resolution.
    /// </summary>
    public void ResolveLlamaServerPath()
    {
        // Check the default ~/.llama/ directory first
        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llama");
        if (Directory.Exists(defaultPath))
        {
            // Check if it contains llama-server binary
            var serverPath = Path.Combine(defaultPath, "llama-server");
            if (File.Exists(serverPath))
            {
                LlamaCppDirectory = defaultPath;
                return;
            }
        }

        // Check ~/.llama-swap/ for llama.cpp
        var swapPath = Path.Combine(UserDirectory, "llama.cpp");
        if (Directory.Exists(swapPath))
        {
            var serverPath = Path.Combine(swapPath, "llama-server");
            if (File.Exists(serverPath))
            {
                LlamaCppDirectory = swapPath;
                return;
            }
        }

        // Check if llama-server is in the llama-swap app directory
        if (Directory.Exists(AppDirectory))
        {
            var serverPath = Path.Combine(AppDirectory, "llama-server");
            if (File.Exists(serverPath))
            {
                LlamaCppDirectory = AppDirectory;
                return;
            }
        }

        // M4: Do NOT resolve from PATH — only trust known directories
        // This prevents an attacker from placing a malicious llama-server in PATH
        LlamaCppDirectory = null;
    }

    public void RefreshPaths() => ResolvePaths();
    public void DetectApiUrl() => _ = Task.Run(async () => ApiBaseUrl = await DetectApiBaseUrlAsync());
    public void DetectLlamaServerUrl() => _ = Task.Run(async () => LlamaServerBaseUrl = await DetectLlamaServerBaseUrlAsync());

    /// <summary>
    /// Public alias for dynamic re-detection. Call this from the metrics polling loop
    /// so that when llama-swap switches models (and thus the upstream llama-server port),
    /// the manager picks up the new port automatically.
    /// </summary>
    public void RefreshLlamaServerUrl() => _ = Task.Run(async () => LlamaServerBaseUrl = await DetectLlamaServerBaseUrlAsync());

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
        var existingApi = await DetectApiBaseUrlAsync();
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
                    // H4: Validate executable path before Unblock-File to prevent command injection
                    var exeDir = Path.GetDirectoryName(ExecutablePath);
                    if (string.IsNullOrEmpty(exeDir) || !Directory.Exists(exeDir))
                    {
                        LogMessage?.Invoke($"[manager] cannot unblock: directory not found for {ExecutablePath}");
                    }
                    else
                    {
                        // Verify the file is actually in the expected directory
                        var resolvedName = Path.GetFileName(ExecutablePath);
                        var actualPath = Path.Combine(exeDir, resolvedName);
                        if (!File.Exists(actualPath))
                        {
                            LogMessage?.Invoke($"[manager] cannot unblock: file not found at {actualPath}");
                        }
                        else
                        {
                            var psiUnblock = new ProcessStartInfo
                            {
                                FileName = "powershell.exe",
                                Arguments = $"-NoProfile -NonInteractive -Command \"Unblock-File -Path '{actualPath}'\"",
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                CreateNoWindow = true,
                                WindowStyle = ProcessWindowStyle.Hidden
                            };
                            var proc = Process.Start(psiUnblock);
                            if (proc is not null)
                            {
                                proc.WaitForExit(5000);
                            }
                        }
                    }
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

            // H3: Track managed PID
            lock (_managedPids)
            {
                _managedPids.Add(_process.Id);
            }

            var apiReady = await WaitForApiReadyAsync(15);

            if (apiReady)
            {
                ApiBaseUrl = await DetectApiBaseUrlAsync();
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
            var api = Task.Run(async () => await DetectApiBaseUrlAsync()).GetAwaiter().GetResult();
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

        // H3: Only stop processes we actually manage
        int[] managedPids;
        lock (_managedPids)
        {
            managedPids = _managedPids.ToArray();
        }

        var processes = new List<Process>();
        if (managedPids.Length > 0)
        {
            foreach (var pid in managedPids)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    if (!p.HasExited)
                        processes.Add(p);
                }
                catch { /* Process already exited */ }
            }
        }

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
                    // UseShellExecute=true avoids stdout/stderr redirect deadlock on Windows.
                    var psi = new ProcessStartInfo
                    {
                        FileName = "taskkill.exe",
                        Arguments = $"/T /PID {p.Id}",
                        UseShellExecute = true,
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
            if (!IsRunning() && await DetectApiBaseUrlAsync() is null)
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

        // H3: Only kill processes we manage
        int[] managedPids;
        lock (_managedPids)
        {
            managedPids = _managedPids.ToArray();
        }

        foreach (var pid in managedPids)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                if (!p.HasExited)
                {
                    LogMessage?.Invoke($"[manager] kill pid={pid} name={p.ProcessName}");
                    p.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[manager] kill failed pid={pid}: {ex.Message}");
            }
        }

        var deadline = DateTime.Now.AddSeconds(8);
        while (DateTime.Now < deadline)
        {
            if (!IsRunning() && await DetectApiBaseUrlAsync() is null)
            {
                LogMessage?.Invoke("[manager] all llama processes stopped");
                ApiBaseUrl = null;
                SetStatus(LlamaSwapStatus.Stopped);
                return true;
            }
            await Task.Delay(250);
        }

        LogProcessSnapshot("still running after kill");
        return !IsRunning() && (await DetectApiBaseUrlAsync()) is null;
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
        var baseUrl = await DetectApiBaseUrlAsync();
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
        var baseUrl = await DetectApiBaseUrlAsync();
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
                var proc = Process.Start(psi);
                return proc?.WaitForExitAsync() ?? Task.CompletedTask;
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
            var baseUrl = await DetectApiBaseUrlAsync();
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
        // H3: Only kill managed PIDs
        int[] managedPids;
        lock (_managedPids)
        {
            managedPids = _managedPids.ToArray();
        }
        foreach (var pid in managedPids)
        {
            try { var p = Process.GetProcessById(pid); p.Kill(true); } catch { }
        }
        await Task.Delay(1000);
        SetStatus(LlamaSwapStatus.Stopped);
    }

    public async Task<string?> DetectApiBaseUrlAsync()
    {
        // llama-swap default port — just test it directly
        if (await TestEndpointAsync("http://127.0.0.1:8080"))
            return "http://127.0.0.1:8080";

        // Fallback: try to find llama-swap process and detect port
        var swapProcesses = Process.GetProcessesByName("llama-swap");
        foreach (var swap in swapProcesses)
        {
            try
            {
                var ports = GetListeningPorts(swap.Id);
                foreach (var port in ports)
                {
                    var baseUrl = $"http://127.0.0.1:{port}";
                    if (await TestEndpointAsync(baseUrl))
                        return baseUrl;
                }
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Detects the upstream llama-server URL by querying llama-swap's /running endpoint.
    /// The /running response includes a "proxy" field with the llama-server address.
    /// Falls back to port scanning if llama-swap is unreachable or no model is loaded.
    /// </summary>
    public async Task<string?> DetectLlamaServerBaseUrlAsync()
    {
        // Primary: query llama-swap /running for the upstream proxy URL
        var swapBaseUrl = await DetectApiBaseUrlAsync();
        if (swapBaseUrl is not null)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var response = await http.GetAsync($"{swapBaseUrl}/running");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    // Extract "proxy" from the first running entry:
                    // {"running":[{"model":"...","proxy":"http://localhost:5801",...}]}
                    var proxyMatch = System.Text.RegularExpressions.Regex.Match(
                        json,
                        "\"proxy\"\\s*:\\s*\"([^\"]+)\"");
                    if (proxyMatch.Success)
                    {
                        var proxyUrl = proxyMatch.Groups[1].Value;
                        // Normalize: llama-swap returns "localhost", ensure we use 127.0.0.1
                        proxyUrl = proxyUrl.Replace("localhost", "127.0.0.1");
                        return proxyUrl;
                    }
                }
            }
            catch { }
        }

        // Fallback: port scan 5800-5900 for llama-server /health
        for (var port = 5800; port <= 5900; port++)
        {
            var baseUrl = $"http://127.0.0.1:{port}";
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                var response = await http.GetAsync($"{baseUrl}/health");
                if (response.IsSuccessStatusCode)
                    return baseUrl;
            }
            catch { }
        }

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
                // Guard against infinite hang: 5s timeout on the whole operation.
                var exited = proc?.WaitForExit(5000) ?? false;
                if (!exited)
                {
                    try { proc?.Kill(true); } catch { }
                    return ports;
                }
                var output = proc?.StandardOutput.ReadToEnd();
                if (output is not null)
                {
                    foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(line.Trim(), out var port))
                            ports.Add(port);
                    }
                }
            }
            else
            {
                // CRITICAL: lsof -p on macOS returns ALL system ports, not just for the process.
                // We must filter by the COMMAND column matching the target process name.
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
                        // Only accept lines that start with the target process name
                        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 1) continue;
                        var cmd = parts[0];
                        // Filter: only lines where COMMAND matches the target process
                        if (cmd != "llama-swap" && cmd != "llama-server" && cmd != "LlamaSwapManager.Desktop")
                            continue;

                        // Now extract port from the NAME column (last field)
                        var namePart = parts[^1];
                        if ((namePart.StartsWith("*:") || namePart.StartsWith("127.0.0.1:")) && namePart.Contains(':'))
                        {
                            var portStr = namePart.Split(':')[^1];
                            if (int.TryParse(portStr, out var port))
                                ports.Add(port);
                        }
                    }
                }
            }
        }
        catch { }

        return ports;
    }

    private async Task<bool> TestEndpointAsync(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await http.GetAsync($"{baseUrl}/running");
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
        // H3: Remove this PID from managed list
        if (_process is not null)
        {
            lock (_managedPids)
            {
                _managedPids.Remove(_process.Id);
            }
        }

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
        return Process.GetProcessesByName("llama-swap").Length > 0 || ApiBaseUrl is not null;
    }

    public void Dispose() { }
}
