using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

internal sealed class LlamaCppVersionDetector
{
    private readonly LlamaCppPlatformConfigurator _platformConfigurator;
    private readonly Action<string>? _log;

    public LlamaCppVersionDetector(
        LlamaCppPlatformConfigurator platformConfigurator,
        Action<string>? log = null)
    {
        _platformConfigurator = platformConfigurator;
        _log = log;
    }

    public async Task<string?> DetectAsync(
        string targetDirectory,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        var serverPath = Path.Combine(targetDirectory, "llama-server");
        if (!File.Exists(serverPath))
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return null;

            serverPath += ".exe";
            if (!File.Exists(serverPath))
                return null;
        }

        _platformConfigurator.RepairDylibLinks(targetDirectory);
        return await DetectFromProcessAsync(serverPath, ct);
    }

    internal static string? ParseVersion(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var match = Regex.Match(output, @"version:\s+(\d+)", RegexOptions.IgnoreCase);
        if (match.Success)
            return $"b{match.Groups[1].Value}";

        match = Regex.Match(output, @"\(([0-9a-fA-F]{5,})\)");
        if (!match.Success)
            return null;

        var hash = match.Groups[1].Value;
        return $"b{hash[..Math.Min(5, hash.Length)]}";
    }

    private async Task<string?> DetectFromProcessAsync(
        string serverPath,
        CancellationToken ct)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = serverPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--version");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                _log?.Invoke("[llama.cpp] Version detection could not start llama-server");
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            var output = await stderrTask + await stdoutTask;
            return ParseVersion(output);
        }
        catch (TimeoutException)
        {
            _log?.Invoke("[llama.cpp] Version detection timed out");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log?.Invoke(
                $"[llama.cpp] Version detection failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
