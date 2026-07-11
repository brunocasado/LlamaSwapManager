using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

internal sealed class LlamaCppPlatformConfigurator
{
    private readonly Action<string>? _log;

    public LlamaCppPlatformConfigurator(Action<string>? log = null)
    {
        _log = log;
    }

    public void ConfigureBinaries(string directory, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return;

        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        RebuildDylibLinks(directory, ct);

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            if (!IsExecutableBinaryName(name))
                continue;

            try
            {
                var mode = File.GetUnixFileMode(file);
                mode |= UnixFileMode.UserExecute |
                        UnixFileMode.GroupExecute |
                        UnixFileMode.OtherExecute;
                File.SetUnixFileMode(file, mode);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"[llama.cpp] Permission update failed for {name}: {ex.Message}");
            }
        }
    }

    public void RepairDylibLinks(string directory)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        RebuildDylibLinks(directory, CancellationToken.None);
    }

    public async Task RemoveQuarantineAsync(string path, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/xattr",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-dr");
            startInfo.ArgumentList.Add("com.apple.quarantine");
            startInfo.ArgumentList.Add(path);

            using var process = Process.Start(startInfo);
            if (process is not null)
                await process.WaitForExitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"[llama.cpp] Quarantine removal warning: {ex.Message}");
        }
    }

    public async Task<bool> VerifyCodesignAsync(string executablePath, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS())
            return true;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/codesign",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--verify");
            startInfo.ArgumentList.Add("--verbose=2");
            startInfo.ArgumentList.Add(executablePath);

            using var process = Process.Start(startInfo);
            if (process is null)
                return false;

            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            return process.ExitCode == 0;
        }
        catch (TimeoutException)
        {
            _log?.Invoke("[llama.cpp] codesign verification timed out");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"[llama.cpp] codesign verification failed: {ex.Message}");
            return false;
        }
    }

    private void RebuildDylibLinks(string directory, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        foreach (var file in Directory.EnumerateFiles(directory, "*.dylib"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (IsSymlink(file))
                    File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"[llama.cpp] Symlink cleanup warning for {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        var groups = FindNewestDylibs(directory);
        foreach (var group in groups.Values)
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileNameWithoutExtension(group.Path);
            var parts = name.Split('.');
            var firstLinkName = string.Join(".", parts.Take(parts.Length - 1)) + ".dylib";
            CreateSymlink(Path.Combine(directory, firstLinkName), name + ".dylib");

            var secondLinkName = string.Join(".", parts.Take(parts.Length - 2)) + ".dylib";
            CreateSymlink(Path.Combine(directory, secondLinkName), firstLinkName);
        }
    }

    internal static Dictionary<string, (string Path, int Minor, int Patch)> FindNewestDylibs(
        string directory)
    {
        var groups = new Dictionary<string, (string Path, int Minor, int Patch)>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.dylib"))
        {
            if (IsSymlink(file))
                continue;

            var name = Path.GetFileNameWithoutExtension(file);
            var parts = name.Split('.');
            if (parts.Length < 4 ||
                !int.TryParse(parts[^3], out _) ||
                !int.TryParse(parts[^2], out var minor) ||
                !int.TryParse(parts[^1], out var patch) ||
                (minor == 0 && patch == 0))
            {
                continue;
            }

            var baseName = string.Join(".", parts.Take(parts.Length - 2));
            if (!groups.TryGetValue(baseName, out var current) ||
                current.Minor < minor ||
                (current.Minor == minor && current.Patch < patch))
            {
                groups[baseName] = (file, minor, patch);
            }
        }

        return groups;
    }

    private static bool IsExecutableBinaryName(string name) =>
        name.StartsWith("llama", StringComparison.Ordinal) ||
        name.StartsWith("ggml", StringComparison.Ordinal) ||
        name.Equals("rpc-server", StringComparison.Ordinal);

    private static bool IsSymlink(string path)
    {
        try { return new FileInfo(path).LinkTarget is not null; }
        catch { return false; }
    }

    private void CreateSymlink(string linkPath, string target)
    {
        try
        {
            if (File.Exists(linkPath) || IsSymlink(linkPath))
                File.Delete(linkPath);

            File.CreateSymbolicLink(linkPath, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"[llama.cpp] Symlink creation warning for {Path.GetFileName(linkPath)}: {ex.Message}");
        }
    }
}
