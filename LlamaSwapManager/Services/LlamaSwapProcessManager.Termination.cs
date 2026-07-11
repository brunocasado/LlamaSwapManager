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

public partial class LlamaSwapProcessManager : IDisposable
{
    private void LogProcessSnapshot(string reason)
        {
            try
            {
                using var processes = new ProcessCollection(GetManagedLlamaProcesses());
                var snapshot = processes.Processes
                    .Select(process => $"{process.ProcessName}:{process.Id}")
                    .DefaultIfEmpty("none");
                LogMessage?.Invoke(
                    $"[manager] {reason} | api={ApiBaseUrl ?? "none"} | processes={string.Join(", ", snapshot)}");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[manager] snapshot failed: {ex.Message}");
            }
        }
    
    
        /// <summary>
        /// Collect every running llama-swap / llama-server process by name.
        /// Orphans from previous Manager sessions must be included or Stop/Quit hang forever.
        /// </summary>
        private List<Process> GetManagedLlamaProcesses()
        {
            var map = new Dictionary<int, Process>();
            HashSet<int> ownedPids;
            lock (_managedPids)
            {
                ownedPids = new HashSet<int>(_managedPids);
            }

            void AddRange(IEnumerable<Process> processes, string? expectedPath)
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (process.HasExited)
                        {
                            process.Dispose();
                            continue;
                        }

                        var owned = ownedPids.Contains(process.Id);
                        var processPath = TryGetProcessPath(process);
                        if (owned || IsExpectedExecutable(processPath, expectedPath))
                        {
                            map[process.Id] = process;
                        }
                        else
                        {
                            process.Dispose();
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        process.Dispose();
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        process.Dispose();
                    }
                }
            }

            AddRange(Process.GetProcessesByName("llama-swap"), ExecutablePath);
            var serverName = OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";
            var serverPath = string.IsNullOrWhiteSpace(LlamaCppDirectory)
                ? null
                : Path.Combine(LlamaCppDirectory, serverName);
            AddRange(Process.GetProcessesByName("llama-server"), serverPath);
            return map.Values.ToList();
        }

        internal static bool IsExpectedExecutable(string? processPath, string? expectedPath)
        {
            if (string.IsNullOrWhiteSpace(processPath) || string.IsNullOrWhiteSpace(expectedPath))
                return false;

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            try
            {
                return string.Equals(Path.GetFullPath(processPath), Path.GetFullPath(expectedPath), comparison);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        private static string? TryGetProcessPath(Process process)
        {
            try
            {
                return process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                return null;
            }
        }

        private void ClearManagedPids()
        {
            lock (_managedPids)
            {
                _managedPids.Clear();
            }
            _process = null;
        }
    
        private async Task<bool> GracefullyStopAllLlamaProcessesAsync(string reason, int timeoutSeconds = 3)
        {
            LogMessage?.Invoke($"[manager] gracefully stopping llama processes: {reason}");
    
            var processes = GetManagedLlamaProcesses();
            if (processes.Count == 0)
            {
                ClearManagedPids();
                ApiBaseUrl = null;
                LlamaServerBaseUrl = null;
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
                        var psi = new ProcessStartInfo
                        {
                            FileName = "taskkill.exe",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        };
                        psi.ArgumentList.Add("/T");
                        psi.ArgumentList.Add("/PID");
                        psi.ArgumentList.Add(p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        using var proc = Process.Start(psi);
                        if (proc is not null)
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                            try { await proc.WaitForExitAsync(cts.Token); }
                            catch (OperationCanceledException) { }
                        }
                    }
                    else
                    {
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
    
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (!IsRunning())
                {
                    LogMessage?.Invoke("[manager] graceful stop completed");
                    ClearManagedPids();
                    ApiBaseUrl = null;
                    LlamaServerBaseUrl = null;
                    SetStatus(LlamaSwapStatus.Stopped);
                    return true;
                }
                await Task.Delay(200);
            }
    
            LogProcessSnapshot("still running after graceful stop");
            return false;
        }
    
        private async Task<bool> KillAllLlamaProcessesAsync(string reason)
        {
            LogMessage?.Invoke($"[manager] killing llama processes: {reason}");
    
            foreach (var p in GetManagedLlamaProcesses())
            {
                try
                {
                    if (!p.HasExited)
                    {
                        LogMessage?.Invoke($"[manager] kill pid={p.Id} name={p.ProcessName}");
                        p.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"[manager] kill failed pid={p.Id}: {ex.Message}");
                }
            }
    
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (!IsRunning())
                {
                    LogMessage?.Invoke("[manager] all llama processes stopped");
                    ClearManagedPids();
                    ApiBaseUrl = null;
                    LlamaServerBaseUrl = null;
                    SetStatus(LlamaSwapStatus.Stopped);
                    return true;
                }
                await Task.Delay(200);
            }
    
            LogProcessSnapshot("still running after kill");
            var done = !IsRunning();
            ClearManagedPids();
            ApiBaseUrl = null;
            LlamaServerBaseUrl = null;
            if (done)
                SetStatus(LlamaSwapStatus.Stopped);
            return done;
        }
    
        public async Task<bool> StopAsync()
        {
            LogProcessSnapshot("stop requested");
            SetStatus(LlamaSwapStatus.Stopping);
            LogMessage?.Invoke("[manager] stopping llama-swap...");
    
            try
            {
                try
                {
                    await TryUnloadModelAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (TimeoutException)
                {
                    LogMessage?.Invoke("[manager] unload budget exceeded — forcing stop");
                }
                catch (OperationCanceledException)
                {
                    LogMessage?.Invoke("[manager] unload cancelled — forcing stop");
                }
    
                var stopped = await GracefullyStopAllLlamaProcessesAsync("stop", timeoutSeconds: 3);
                if (!stopped)
                    stopped = await KillAllLlamaProcessesAsync("stop fallback");
    
                // Always leave a terminal state so Start/Stop/Restart buttons unstick.
                ClearManagedPids();
                ApiBaseUrl = null;
                LlamaServerBaseUrl = null;
                SetStatus(stopped ? LlamaSwapStatus.Stopped : LlamaSwapStatus.Error);
                LogProcessSnapshot(stopped ? "stop completed" : "stop failed");
                return stopped;
            }
            catch (Exception ex)
            {
                ClearManagedPids();
                ApiBaseUrl = null;
                LlamaServerBaseUrl = null;
                SetStatus(LlamaSwapStatus.Error);
                LogMessage?.Invoke($"[manager] error stopping: {ex.Message}");
                return false;
            }
        }
    
        /// <summary>
        /// Synchronous hard-stop used by Quit. Always enumerates by process name and
        /// never waits on managed-PID-only bookkeeping.
        /// </summary>
        public async Task ForceStopForQuitAsync()
        {
            LogMessage?.Invoke("[manager] force stop for quit");
            try
            {
                await KillAllLlamaProcessesAsync("quit force");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[manager] force stop for quit failed: {ex.Message}");
            }
            finally
            {
                ClearManagedPids();
                ApiBaseUrl = null;
                LlamaServerBaseUrl = null;
                SetStatus(LlamaSwapStatus.Stopped);
            }
        }
    
        public async Task<bool> RestartAsync()
        {
            LogMessage?.Invoke("[manager] restart requested");
            LogProcessSnapshot("before restart");
    
            var stopped = await StopAsync();
            if (!stopped && IsRunning())
            {
                LogMessage?.Invoke("[manager] restart: first stop incomplete — force killing");
                stopped = await KillAllLlamaProcessesAsync("restart force");
            }
    
            if (IsRunning())
            {
                LogMessage?.Invoke("[manager] restart aborted: could not stop existing processes");
                SetStatus(LlamaSwapStatus.Error);
                return false;
            }
    
            await Task.Delay(400);
            ResolvePaths();
            return await StartAsync();
        }
}
