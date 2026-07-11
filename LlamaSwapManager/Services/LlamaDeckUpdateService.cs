using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

internal sealed record LlamaDeckUpdateInfo(
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string AssetName,
    string AssetUrl,
    string? AssetDigest,
    bool IsUpdateAvailable);

internal sealed class LlamaDeckUpdateService : IDisposable
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/brunocasado/LlamaDeck/releases/latest";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _currentVersion;
    private readonly string _baseDirectory;
    private readonly string? _processPath;
    private readonly int _processId;

    public LlamaDeckUpdateService(
        HttpClient? httpClient = null,
        string? currentVersion = null,
        string? baseDirectory = null,
        string? processPath = null,
        int? processId = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LlamaDeck/1.0");

        _currentVersion = NormalizeVersion(currentVersion ?? DetectCurrentVersion());
        _baseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        _processPath = processPath ?? Environment.ProcessPath;
        _processId = processId ?? Environment.ProcessId;
    }

    public bool CanInstallUpdate => IsPackagedProcess(_processPath);

    public async Task<LlamaDeckUpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync(LatestReleaseUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = document.RootElement;

        var tagName = root.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(tagName))
            return null;

        var latestVersion = NormalizeVersion(tagName);
        var assetName = GetExpectedAssetName(latestVersion);
        if (assetName is null)
            return new LlamaDeckUpdateInfo(
                _currentVersion,
                latestVersion,
                root.TryGetProperty("html_url", out var releaseElement) ? releaseElement.GetString() ?? string.Empty : string.Empty,
                string.Empty,
                string.Empty,
                null,
                IsNewerVersion(_currentVersion, latestVersion));

        string assetUrl = string.Empty;
        string? digest = null;
        if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsElement.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString()
                    : null;
                if (!string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                assetUrl = asset.TryGetProperty("browser_download_url", out var urlElement)
                    ? urlElement.GetString() ?? string.Empty
                    : string.Empty;
                digest = asset.TryGetProperty("digest", out var digestElement)
                    ? digestElement.GetString()
                    : null;
                break;
            }
        }

        var releaseUrl = root.TryGetProperty("html_url", out var htmlElement)
            ? htmlElement.GetString() ?? string.Empty
            : string.Empty;

        return new LlamaDeckUpdateInfo(
            _currentVersion,
            latestVersion,
            releaseUrl,
            assetName,
            assetUrl,
            digest,
            IsNewerVersion(_currentVersion, latestVersion));
    }

    public async Task<bool> DownloadAndPrepareAsync(
        LlamaDeckUpdateInfo update,
        IProgress<(string Message, int Percentage)>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (!update.IsUpdateAvailable || string.IsNullOrWhiteSpace(update.AssetUrl))
            return false;
        if (!CanInstallUpdate)
            throw new InvalidOperationException(
                "Automatic installation is only available in a packaged LlamaDeck build.");

        EnsureInstallLocationWritable();

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "LlamaDeck",
            $"update-{Guid.NewGuid():N}");
        var stagingDirectory = Path.Combine(tempRoot, "staging");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            var archivePath = Path.Combine(tempRoot, update.AssetName);
            progress?.Report(("Downloading LlamaDeck update...", 10));
            await DownloadAsync(update.AssetUrl, archivePath, progress, ct).ConfigureAwait(false);

            progress?.Report(("Verifying downloaded package...", 65));
            await VerifyDigestAsync(archivePath, update.AssetDigest, ct).ConfigureAwait(false);

            progress?.Report(("Extracting update...", 75));
            ExtractPackage(archivePath, stagingDirectory, ct);

            progress?.Report(("Preparing restart...", 90));
            LaunchUpdateHelper(tempRoot, stagingDirectory);
            progress?.Report(("Update ready. Restarting LlamaDeck...", 100));
            return true;
        }
        catch
        {
            TryDeleteDirectory(tempRoot);
            throw;
        }
    }

    internal static string DetectCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return NormalizeVersion(informational);

        return NormalizeVersion(assembly.GetName().Version?.ToString() ?? "0.0.0");
    }

    internal static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return "0.0.0";

        var value = version.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
            value = value[1..];

        var metadataIndex = value.IndexOfAny(['+', '-']);
        if (metadataIndex >= 0)
            value = value[..metadataIndex];

        return Version.TryParse(value, out var parsed)
            ? $"{parsed.Major}.{parsed.Minor}.{Math.Max(0, parsed.Build)}"
            : "0.0.0";
    }

    internal static bool IsNewerVersion(string currentVersion, string latestVersion)
    {
        return Version.TryParse(NormalizeVersion(currentVersion), out var current)
               && Version.TryParse(NormalizeVersion(latestVersion), out var latest)
               && latest > current;
    }

    internal static string? GetExpectedAssetName(string version)
    {
        var suffix = GetPlatformAssetSuffix();
        return suffix is null ? null : $"LlamaDeck-{NormalizeVersion(version)}-{suffix}";
    }

    internal static string? GetPlatformAssetSuffix()
    {
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return "windows-x64.zip";
        if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return "linux-x64.tar.gz";
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return "macos-arm64.zip";
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return "macos-x64.zip";

        return null;
    }

    internal static void ExtractPackage(
        string archivePath,
        string stagingDirectory,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(stagingDirectory);
        if (Directory.EnumerateFileSystemEntries(stagingDirectory).Any())
            throw new InvalidOperationException($"Update staging directory must be empty: {stagingDirectory}");

        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            ArchiveExtractor.ExtractTarGz(archivePath, stagingDirectory, ct);
            return;
        }

        ZipFile.ExtractToDirectory(archivePath, stagingDirectory, overwriteFiles: true);
        ct.ThrowIfCancellationRequested();
    }

    private async Task DownloadAsync(
        string url,
        string destination,
        IProgress<(string Message, int Percentage)>? progress,
        CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);

        var buffer = new byte[81920];
        long copied = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                break;

            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            copied += read;
            if (total is > 0)
            {
                var percentage = 10 + (int)Math.Min(50, copied * 50 / total.Value);
                progress?.Report(("Downloading LlamaDeck update...", percentage));
            }
        }
    }

    private static async Task VerifyDigestAsync(
        string archivePath,
        string? digest,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(digest)
            || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var expected = digest["sha256:".Length..].Trim();
        await using var stream = File.OpenRead(archivePath);
        var actualBytes = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        var actual = Convert.ToHexString(actualBytes);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded LlamaDeck package failed SHA-256 validation.");
    }

    private void LaunchUpdateHelper(string tempRoot, string stagingDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            LaunchWindowsUpdateHelper(tempRoot, stagingDirectory);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            LaunchMacUpdateHelper(tempRoot, stagingDirectory);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            LaunchLinuxUpdateHelper(tempRoot, stagingDirectory);
            return;
        }

        throw new PlatformNotSupportedException("Automatic LlamaDeck updates are not supported on this platform.");
    }

    private void LaunchWindowsUpdateHelper(string tempRoot, string stagingDirectory)
    {
        var executableName = Path.GetFileName(_processPath)
            ?? "LlamaSwapManager.Desktop.exe";
        var scriptPath = Path.Combine(tempRoot, "apply-update.ps1");
        var backupDirectory = Path.Combine(tempRoot, "backup");
        var executablePath = Path.Combine(_baseDirectory, executableName);
        var script = string.Join(Environment.NewLine, new[]
        {
            "$ErrorActionPreference = 'Stop'",
            $"Wait-Process -Id {_processId}",
            "Start-Sleep -Milliseconds 500",
            "try {",
            $"    New-Item -ItemType Directory -Force -Path '{EscapePowerShell(backupDirectory)}' | Out-Null",
            $"    Copy-Item -Path '{EscapePowerShell(_baseDirectory)}\\*' -Destination '{EscapePowerShell(backupDirectory)}' -Recurse -Force",
            $"    Copy-Item -Path '{EscapePowerShell(stagingDirectory)}\\*' -Destination '{EscapePowerShell(_baseDirectory)}' -Recurse -Force",
            $"    Start-Process '{EscapePowerShell(executablePath)}'",
            "} catch {",
            $"    if (Test-Path -LiteralPath '{EscapePowerShell(backupDirectory)}') {{",
            $"        Copy-Item -Path '{EscapePowerShell(backupDirectory)}\\*' -Destination '{EscapePowerShell(_baseDirectory)}' -Recurse -Force",
            "    }",
            $"    Start-Process '{EscapePowerShell(executablePath)}'",
            "    throw",
            "}",
            "Start-Sleep -Milliseconds 500",
            $"Remove-Item -LiteralPath '{EscapePowerShell(tempRoot)}' -Recurse -Force -ErrorAction SilentlyContinue"
        });
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not launch the Windows update helper.");
    }

    private void LaunchMacUpdateHelper(string tempRoot, string stagingDirectory)
    {
        var currentBundle = FindMacAppBundle(_baseDirectory)
            ?? throw new InvalidOperationException("Could not locate the current LlamaDeck.app bundle.");
        var stagedBundle = Directory
            .EnumerateDirectories(stagingDirectory, "LlamaDeck.app", SearchOption.AllDirectories)
            .FirstOrDefault()
            ?? throw new InvalidDataException("The macOS package does not contain LlamaDeck.app.");

        var backupBundle = Path.Combine(tempRoot, "LlamaDeck.backup.app");
        var scriptPath = Path.Combine(tempRoot, "apply-update.sh");
        var script = $"""
#!/bin/sh
set -e
while kill -0 {_processId} 2>/dev/null; do sleep 0.2; done
rm -rf {ShellQuote(backupBundle)}
mv {ShellQuote(currentBundle)} {ShellQuote(backupBundle)}
if ditto {ShellQuote(stagedBundle)} {ShellQuote(currentBundle)}; then
    rm -rf {ShellQuote(backupBundle)}
    open {ShellQuote(currentBundle)}
else
    rm -rf {ShellQuote(currentBundle)}
    mv {ShellQuote(backupBundle)} {ShellQuote(currentBundle)}
    open {ShellQuote(currentBundle)}
    exit 1
fi
rm -rf {ShellQuote(tempRoot)}
""";
        File.WriteAllText(scriptPath, script);
        SetUnixExecutable(scriptPath);
        StartDetachedShell(scriptPath);
    }

    private void LaunchLinuxUpdateHelper(string tempRoot, string stagingDirectory)
    {
        var executableName = Path.GetFileName(_processPath)
            ?? "LlamaSwapManager.Desktop";
        var executablePath = Path.Combine(_baseDirectory, executableName);
        var backupDirectory = Path.Combine(tempRoot, "backup");
        var scriptPath = Path.Combine(tempRoot, "apply-update.sh");
        var script = $"""
#!/bin/sh
set -e
while kill -0 {_processId} 2>/dev/null; do sleep 0.2; done
mkdir -p {ShellQuote(backupDirectory)}
cp -a {ShellQuote(_baseDirectory + "/.")} {ShellQuote(backupDirectory)}
if cp -a {ShellQuote(stagingDirectory + "/.")} {ShellQuote(_baseDirectory)}; then
    chmod +x {ShellQuote(executablePath)}
    nohup {ShellQuote(executablePath)} >/dev/null 2>&1 &
else
    cp -a {ShellQuote(backupDirectory + "/.")} {ShellQuote(_baseDirectory)}
    chmod +x {ShellQuote(executablePath)}
    nohup {ShellQuote(executablePath)} >/dev/null 2>&1 &
    exit 1
fi
rm -rf {ShellQuote(tempRoot)}
""";
        File.WriteAllText(scriptPath, script);
        SetUnixExecutable(scriptPath);
        StartDetachedShell(scriptPath);
    }

    private static void StartDetachedShell(string scriptPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(scriptPath);
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not launch the update helper.");
    }

    private static void SetUnixExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private void EnsureInstallLocationWritable()
    {
        var targetDirectory = _baseDirectory;
        if (OperatingSystem.IsMacOS())
        {
            var bundle = FindMacAppBundle(_baseDirectory)
                ?? throw new InvalidOperationException("Could not locate the current LlamaDeck.app bundle.");
            targetDirectory = Directory.GetParent(bundle)?.FullName
                ?? throw new InvalidOperationException("Could not locate the LlamaDeck.app parent directory.");
        }

        var probePath = Path.Combine(targetDirectory, $".llamadeck-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probePath, "ok");
        }
        catch (Exception ex)
        {
            throw new UnauthorizedAccessException(
                $"LlamaDeck cannot update this installation because the directory is not writable: {targetDirectory}",
                ex);
        }
        finally
        {
            try
            {
                if (File.Exists(probePath))
                    File.Delete(probePath);
            }
            catch
            {
                // Best-effort cleanup of the write probe.
            }
        }
    }

    private static string? FindMacAppBundle(string baseDirectory)
    {
        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }

    private static bool IsPackagedProcess(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            return false;

        var name = processPath.Replace('\\', '/').Split('/').Last();
        return !name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
               && !name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
               && name.StartsWith("LlamaSwapManager.Desktop", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''");

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'") + "'";

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Temporary update files are best-effort cleanup.
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
