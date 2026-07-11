using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

public partial class LlamaSwapProcessManager : IDisposable
{
    public async Task<string?> GetRunningModelAsync()
    {
        var baseUrl = await DetectApiBaseUrlAsync();
        if (baseUrl is null)
            return null;

        try
        {
            using var response = await _localHttpClient.GetAsync($"{baseUrl}/running");
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            if (!document.RootElement.TryGetProperty("running", out var running) ||
                running.ValueKind != JsonValueKind.Array ||
                running.GetArrayLength() == 0)
                return null;

            var first = running[0];
            return first.TryGetProperty("model", out var model)
                ? model.GetString()
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogMessage?.Invoke($"[manager] running model detection failed: {ex.Message}");
            return null;
        }
    }

        private async Task TryUnloadModelAsync()
        {
            var baseUrl = await DetectApiBaseUrlAsync();
            if (baseUrl is null) { LogMessage?.Invoke("[manager] unload skipped: API not detected"); return; }
    
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                LogMessage?.Invoke($"[manager] unloading model via {baseUrl}/api/models/unload");
                using var response = await _localHttpClient.PostAsync(
                    $"{baseUrl}/api/models/unload",
                    null,
                    cts.Token);
                LogMessage?.Invoke($"[manager] unload response: {(int)response.StatusCode} {response.StatusCode}");
                await Task.Delay(response.IsSuccessStatusCode ? 300 : 100, cts.Token);
            }
            catch (OperationCanceledException)
            {
                LogMessage?.Invoke("[manager] unload timed out — continuing stop");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                LogMessage?.Invoke($"[manager] unload failed: {ex.Message}");
            }
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
            using var managedProcesses = new ProcessCollection(GetManagedLlamaProcesses());
            var swapProcesses = managedProcesses.Processes
                .Where(process => process.ProcessName.Equals("llama-swap", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (swapProcesses.Count == 0)
                return null;

            if (await TestEndpointAsync("http://127.0.0.1:8080"))
                return "http://127.0.0.1:8080";

            foreach (var swap in swapProcesses)
            {
                foreach (var port in await GetListeningPortsAsync(swap.Id))
                {
                    var baseUrl = $"http://127.0.0.1:{port}";
                    if (await TestEndpointAsync(baseUrl))
                        return baseUrl;
                }
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
            var swapBaseUrl = await DetectApiBaseUrlAsync();
            if (swapBaseUrl is not null)
            {
                try
                {
                    using var response = await _localHttpClient.GetAsync($"{swapBaseUrl}/running");
                    if (response.IsSuccessStatusCode)
                    {
                        await using var stream = await response.Content.ReadAsStreamAsync();
                        using var document = await JsonDocument.ParseAsync(stream);
                        if (document.RootElement.TryGetProperty("running", out var running) &&
                            running.ValueKind == JsonValueKind.Array &&
                            running.GetArrayLength() > 0 &&
                            running[0].TryGetProperty("proxy", out var proxy))
                        {
                            return proxy.GetString()?.Replace(
                                "localhost",
                                "127.0.0.1",
                                StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                    LogMessage?.Invoke($"[manager] upstream URL detection failed: {ex.Message}");
                }
            }

            using var managedProcesses = new ProcessCollection(GetManagedLlamaProcesses());
            foreach (var server in managedProcesses.Processes.Where(process =>
                         process.ProcessName.Equals("llama-server", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var port in await GetListeningPortsAsync(server.Id))
                {
                    var baseUrl = $"http://127.0.0.1:{port}";
                    if (await TestHealthEndpointAsync(baseUrl))
                        return baseUrl;
                }
            }

            return null;
        }

        private async Task<HashSet<int>> GetListeningPortsAsync(
            int processId,
            CancellationToken cancellationToken = default)
        {
            var ports = new HashSet<int>();

            try
            {
                ProcessStartInfo startInfo;
                if (OperatingSystem.IsWindows())
                {
                    startInfo = new ProcessStartInfo
                    {
                        FileName = "netstat.exe",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    startInfo.ArgumentList.Add("-ano");
                    startInfo.ArgumentList.Add("-p");
                    startInfo.ArgumentList.Add("tcp");
                }
                else
                {
                    startInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/sbin/lsof",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    startInfo.ArgumentList.Add("-a");
                    startInfo.ArgumentList.Add("-iTCP");
                    startInfo.ArgumentList.Add("-sTCP:LISTEN");
                    startInfo.ArgumentList.Add("-p");
                    startInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    startInfo.ArgumentList.Add("-nP");
                }

                using var process = Process.Start(startInfo);
                if (process is null)
                    return ports;

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                var output = await outputTask;

                if (OperatingSystem.IsWindows())
                    ParseWindowsNetstat(output, processId, ports);
                else
                    ParseLsof(output, ports);
            }
            catch (TimeoutException)
            {
                LogMessage?.Invoke($"[manager] port detection timed out for pid={processId}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LogMessage?.Invoke($"[manager] port detection failed for pid={processId}: {ex.Message}");
            }

            return ports;
        }

        internal static void ParseWindowsNetstat(
            string output,
            int processId,
            ISet<int> ports)
        {
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 ||
                    !parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase) ||
                    !parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase) ||
                    !int.TryParse(parts[^1], out var pid) ||
                    pid != processId)
                    continue;

                if (TryParsePort(parts[1], out var port))
                    ports.Add(port);
            }
        }

        internal static void ParseLsof(string output, ISet<int> ports)
        {
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    continue;

                var endpoint = parts.LastOrDefault(part => part.Contains(':'));
                if (endpoint is not null && TryParsePort(endpoint, out var port))
                    ports.Add(port);
            }
        }

        private static bool TryParsePort(string endpoint, out int port)
        {
            port = 0;
            var value = endpoint;
            var arrow = value.IndexOf("->", StringComparison.Ordinal);
            if (arrow >= 0)
                value = value[..arrow];

            var colon = value.LastIndexOf(':');
            if (colon < 0)
                return false;

            var portText = value[(colon + 1)..].TrimEnd(')', ' ');
            return int.TryParse(portText, out port) && port is > 0 and <= 65535;
        }

        private async Task<bool> TestEndpointAsync(string baseUrl)
        {
            try
            {
                using var response = await _localHttpClient.GetAsync($"{baseUrl}/running");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return false;
            }
        }

        private async Task<bool> TestHealthEndpointAsync(string baseUrl)
        {
            try
            {
                using var response = await _localHttpClient.GetAsync($"{baseUrl}/health");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return false;
            }
        }

        private void SetStatus(LlamaSwapStatus newStatus)
        {
            var changed = false;
            lock (_lock)
            {
                if (Status != newStatus)
                {
                    Status = newStatus;
                    changed = true;
                }
            }

            if (changed)
                StatusChanged?.Invoke(newStatus);
        }

        private void OnProcessExited(object? sender, EventArgs e)
        {
            if (sender is Process exitedProcess)
            {
                lock (_managedPids)
                {
                    _managedPids.Remove(exitedProcess.Id);
                }
            }

            SetStatus(LlamaSwapStatus.Stopped);
            ApiBaseUrl = null;
            LlamaServerBaseUrl = null;
        }

        public bool IsRunning()
        {
            using var processes = new ProcessCollection(GetManagedLlamaProcesses());
            return processes.Processes.Count > 0;
        }

        public bool IsLlamaSwapProcessRunning()
        {
            using var processes = new ProcessCollection(GetManagedLlamaProcesses());
            return processes.Processes.Any(process =>
                process.ProcessName.Equals("llama-swap", StringComparison.OrdinalIgnoreCase));
        }

        public bool IsProxyRunning() => IsLlamaSwapProcessRunning() || ApiBaseUrl is not null;

        public void Dispose()
        {
            _process?.Dispose();
            _llamaCppDownloader?.Dispose();
            _localHttpClient.Dispose();
        }

        private sealed class ProcessCollection : IDisposable
        {
            public IReadOnlyList<Process> Processes { get; }

            public ProcessCollection(IReadOnlyList<Process> processes)
            {
                Processes = processes;
            }

            public void Dispose()
            {
                foreach (var process in Processes)
                    process.Dispose();
            }
        }

}
