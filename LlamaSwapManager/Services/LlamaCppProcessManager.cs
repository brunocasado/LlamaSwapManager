using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

internal sealed class LlamaCppProcessManager
{
    private readonly Action<string>? _log;

    public LlamaCppProcessManager(Action<string>? log = null)
    {
        _log = log;
    }

    public async Task StopManagedServerAsync(string targetDirectory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        var serverExe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "llama-server.exe"
            : "llama-server";
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var serverPath = Path.GetFullPath(Path.Combine(targetDirectory, serverExe));
        var processes = FindManagedProcesses(serverPath, comparison);

        if (processes.Count == 0)
        {
            _log?.Invoke("[llama.cpp] No running llama-server processes found");
            return;
        }

        try
        {
            _log?.Invoke($"[llama.cpp] Found {processes.Count} llama-server process(es) to stop");

            foreach (var process in processes)
            {
                ct.ThrowIfCancellationRequested();
                await TryGracefulStopAsync(process, ct);
            }

            foreach (var process in processes)
                await WaitForExitAsync(process, TimeSpan.FromSeconds(5), ct);

            foreach (var process in processes)
            {
                ct.ThrowIfCancellationRequested();
                await ForceStopAsync(process, ct);
            }

            _log?.Invoke("[llama.cpp] llama-server processes stopped");
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    internal static bool IsManagedProcessPath(
        string? processPath,
        string expectedPath,
        StringComparison comparison)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            return false;

        return string.Equals(Path.GetFullPath(processPath), Path.GetFullPath(expectedPath), comparison);
    }

    private static List<Process> FindManagedProcesses(string serverPath, StringComparison comparison)
    {
        var matches = new List<Process>();

        try
        {
            foreach (var process in Process.GetProcessesByName("llama-server"))
            {
                try
                {
                    if (IsManagedProcessPath(process.MainModule?.FileName, serverPath, comparison))
                        matches.Add(process);
                    else
                        process.Dispose();
                }
                catch
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // Process enumeration may be denied by the operating system.
        }

        return matches;
    }

    private async Task TryGracefulStopAsync(Process process, CancellationToken ct)
    {
        try
        {
            _log?.Invoke($"[llama.cpp] Sending graceful stop to llama-server pid={process.Id}");
            if (process.HasExited)
                return;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("/T");
                startInfo.ArgumentList.Add("/PID");
                startInfo.ArgumentList.Add(process.Id.ToString());

                using var taskKill = Process.Start(startInfo);
                if (taskKill is not null)
                    await taskKill.WaitForExitAsync(ct);
            }
            else
            {
                process.Kill(entireProcessTree: false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"[llama.cpp] Graceful stop failed pid={process.Id}: {ex.Message}");
        }
    }

    private static async Task WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            if (!process.HasExited)
                await process.WaitForExitAsync(ct).WaitAsync(timeout, ct);
        }
        catch (TimeoutException) { }
        catch (InvalidOperationException) { }
    }

    private async Task ForceStopAsync(Process process, CancellationToken ct)
    {
        try
        {
            if (process.HasExited)
                return;

            _log?.Invoke($"[llama.cpp] Force killing llama-server pid={process.Id}");
            process.Kill(entireProcessTree: true);
            await WaitForExitAsync(process, TimeSpan.FromSeconds(2), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"[llama.cpp] Force kill failed pid={process.Id}: {ex.Message}");
        }
    }
}
