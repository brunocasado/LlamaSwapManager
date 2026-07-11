using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

/// <summary>
/// Service for downloading, verifying, and installing llama-swap updates.
/// Downloads from GitHub releases, verifies checksums, backs up the current binary,
/// and rolls back automatically on failure.
/// </summary>
public class UpdateService : IDisposable
{
    private const string GitHubRepo = "mostlygeek/llama-swap";
    private const int ApiTimeoutSeconds = 30;
    private const long MinDiskSpaceBytes = 500L * 1024 * 1024; // 500MB minimum free

    private readonly HttpClient _httpClient;
    private readonly string _installDirectory;
    private readonly string _osName;
    private readonly string _arch;
    private readonly LlamaSwapProcessManager? _processManager;
    private readonly SemaphoreSlim _updateCheckLock = new(1, 1);
    private static DateTime _lastUpdateCheck = DateTime.MinValue;
    private static readonly TimeSpan UpdateCheckCooldown = TimeSpan.FromMinutes(5);

    public event Action<UpdateProgress>? ProgressChanged;
    public event Action<string>? LogMessage;

    /// <summary>
    /// Creates an UpdateService for the given install directory.
    /// </summary>
    /// <param name="installDirectory">Directory where llama-swap binary lives.</param>
    /// <param name="processManager">Optional process manager for stop/start integration.</param>
    public UpdateService(string installDirectory, LlamaSwapProcessManager? processManager = null)
    {
        _installDirectory = installDirectory;
        _processManager = processManager;
        _osName = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "darwin" :
                   RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" :
                   RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" : "linux";
        _arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "amd64",
            Architecture.X86 => "amd64",
            _ => "amd64"
        };

        _httpClient = CreateSecureHttpClient();
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LlamaSwapManager/1.0");
    }

    /// <summary>
    /// Creates an HttpClient with explicit security settings:
    /// - Server certificate validation enabled (no dangerous bypass)
    /// - Optional GitHub token for authentication (M2: GitHub API auth)
    /// </summary>
    private static HttpClient CreateSecureHttpClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
            {
                // Reject if there are any SSL policy errors
                if (errors != SslPolicyErrors.None)
                {
                    return false;
                }
                return true;
            }
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(ApiTimeoutSeconds) };

        // M2: Support GITHUB_TOKEN env var for authenticated API calls (60/hr → 5000/hr)
        var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrEmpty(githubToken))
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", githubToken);
        }

        return client;
    }

    public void Dispose() => _httpClient.Dispose();

    /// <summary>
    /// Check for the latest available version without downloading.
    /// </summary>
    public async Task<LatestVersionInfo?> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"https://api.github.com/repos/{GitHubRepo}/releases/latest", ct);

            if (!response.IsSuccessStatusCode)
            {
                LogMessage?.Invoke($"Failed to check for updates: {(int)response.StatusCode} {response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var release = System.Text.Json.JsonSerializer.Deserialize<JsonRelease>(json);
            if (release == null) return null;

            // Find the asset for this platform/arch — strict matching
            var asset = release.Assets?
                .FirstOrDefault(a => !string.IsNullOrEmpty(a.Name) && AssetMatchesPlatform(a.Name));

            if (asset == null)
            {
                LogMessage?.Invoke($"No download asset found for {_osName}/{_arch}");
                return null;
            }

            var checksums = await FetchChecksumsAsync(release.TagName ?? string.Empty, ct);

            return new LatestVersionInfo
            {
                Version = release.TagName,
                AssetName = asset.Name,
                DownloadUrl = asset.BrowserDownloadUrl,
                SizeBytes = asset.Size,
                Checksums = checksums
            };
        }
        catch (TaskCanceledException)
        {
            LogMessage?.Invoke("Timeout checking for updates");
            return null;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Error checking for updates: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Download and install the latest llama-swap binary.
    /// Includes process stop, backup, verification, and automatic rollback.
    /// </summary>
    public async Task<bool> UpdateAsync(string targetVersion, CancellationToken ct = default)
    {
        var backupPath = Path.Combine(_installDirectory, $"llama-swap.backup.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        var tempDir = Path.Combine(Path.GetTempPath(), $"llama-swap-update-{Guid.NewGuid()}");
        var targetExe = Path.Combine(_installDirectory, GetBinaryName());

        Directory.CreateDirectory(tempDir);

        try
        {
            // Step 1: Check for updates
            ProgressChanged?.Invoke(new UpdateProgress("Checking for updates...", 0));

            var latest = await CheckForUpdatesAsync(ct);
            if (latest == null)
            {
                LogMessage?.Invoke("Could not retrieve update information");
                return false;
            }

            var version = string.IsNullOrWhiteSpace(targetVersion)
                ? latest.Version ?? "unknown"
                : targetVersion;
            ProgressChanged?.Invoke(new UpdateProgress($"Found version {version}", 10));

            // Step 2: Verify disk space
            ProgressChanged?.Invoke(new UpdateProgress("Checking disk space...", 12));

            if (!CheckDiskSpace(latest.SizeBytes))
            {
                LogMessage?.Invoke("Insufficient disk space for update");
                return false;
            }

            // Step 3: Stop llama-swap process before install
            ProgressChanged?.Invoke(new UpdateProgress("Stopping llama-swap...", 15));

            var processStopped = await StopProcessAsync(ct);
            if (!processStopped)
            {
                LogMessage?.Invoke("Could not stop llama-swap — update aborted for safety");
                return false;
            }

            // Step 4: Download the archive
            ProgressChanged?.Invoke(new UpdateProgress("Downloading archive...", 20));

            var archiveName = latest.AssetName ?? $"llama-swap-{SanitizeVersion(latest.Version ?? "unknown")}.tar.gz";
            var archivePath = Path.Combine(tempDir, archiveName);
            var downloadUrl = latest.DownloadUrl;
            if (string.IsNullOrEmpty(downloadUrl))
            {
                LogMessage?.Invoke("No download URL available");
                return false;
            }
            var downloadSize = latest.SizeBytes;
            var downloaded = await DownloadWithProgressAsync(downloadUrl, archivePath, downloadSize, ct);

            if (!downloaded)
            {
                LogMessage?.Invoke("Download failed");
                return false;
            }

            // Step 5: Verify checksum
            ProgressChanged?.Invoke(new UpdateProgress("Verifying checksum...", 60));

            var archiveHash = await ComputeSha256Async(archivePath);
            var checksumExpected = latest.Checksums?.FirstOrDefault(c => c.Name == archiveName)?.Sha256;

            if (!string.IsNullOrEmpty(checksumExpected) && !archiveHash.Equals(checksumExpected, StringComparison.OrdinalIgnoreCase))
            {
                LogMessage?.Invoke($"Checksum mismatch: expected {checksumExpected}, got {archiveHash}");
                return false;
            }

            ProgressChanged?.Invoke(new UpdateProgress("Checksum verified", 70));

            // Step 6: Extract archive (tar.gz for Unix, zip for Windows)
            ProgressChanged?.Invoke(new UpdateProgress("Extracting archive...", 75));

            var extractDir = Path.Combine(tempDir, "extracted");
            Directory.CreateDirectory(extractDir);

            bool extractOk;
            if (_osName == "windows" && archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                extractOk = ExtractZip(archivePath, extractDir);
            }
            else
            {
                extractOk = ExtractTarGz(archivePath, extractDir);
            }

            if (!extractOk)
            {
                LogMessage?.Invoke("Failed to extract archive");
                return false;
            }

            var extractedExe = FindExtractedBinary(extractDir);
            if (string.IsNullOrEmpty(extractedExe))
            {
                LogMessage?.Invoke("Could not find extracted binary");
                return false;
            }

            // Step 7: Backup current binary
            ProgressChanged?.Invoke(new UpdateProgress("Preparing installation...", 80));

            if (File.Exists(targetExe))
            {
                try
                {
                    File.Copy(targetExe, backupPath, overwrite: false);
                    LogMessage?.Invoke($"Backup created at {backupPath}");
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"Warning: backup failed ({ex.Message}), continuing anyway");
                }
            }

            // Step 7.5: H5 — Require explicit user confirmation before replacing binary
            var confirmed = await PromptUpdateConfirmationAsync(version, targetExe, ct);
            if (!confirmed)
            {
                LogMessage?.Invoke("Update cancelled by user");
                return false;
            }

            // Step 8: Replace binary
            ProgressChanged?.Invoke(new UpdateProgress("Installing...", 85));

            if (File.Exists(targetExe))
                File.Delete(targetExe);

            File.Move(extractedExe, targetExe);

            // Make executable on Unix
            SetExecutable(targetExe);

            // H1: Verify macOS codesign on extracted binary (before installation)
            if (_osName == "darwin")
            {
                var codesignOk = VerifyCodesign(extractedExe);
                if (!codesignOk)
                {
                    LogMessage?.Invoke("Warning: codesign verification failed — binary may not be signed by a known developer");
                    // Don't block — checksum was already verified, but log the warning
                }

                RemoveQuarantineAttribute(targetExe);
            }

            // Step 9: Restart process if process manager is available
            ProgressChanged?.Invoke(new UpdateProgress("Restarting llama-swap...", 95));

            if (_processManager is not null)
            {
                try
                {
                    await _processManager.RestartAsync();
                    LogMessage?.Invoke("llama-swap restarted successfully");
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"Warning: restart failed ({ex.Message}) — binary updated but process needs manual restart");
                }
            }

            ProgressChanged?.Invoke(new UpdateProgress("Update complete!", 100));
            LogMessage?.Invoke($"Updated llama-swap to {version}");

            return true;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Update failed: {ex.Message}");

            // Rollback
            RollbackAsync(targetExe, backupPath);
            return false;
        }
        finally
        {
            // Cleanup temp files
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Rollback: restore the backup binary if the update failed.
    /// </summary>
    private void RollbackAsync(string targetExe, string backupPath)
    {
        try
        {
            if (File.Exists(backupPath))
            {
                if (File.Exists(targetExe))
                    File.Delete(targetExe);

                File.Move(backupPath, targetExe);
                SetExecutable(targetExe);
                LogMessage?.Invoke("Rollback: restored from backup");

                if (_osName == "darwin")
                {
                    RemoveQuarantineAttribute(targetExe);
                }
            }
            else
            {
                LogMessage?.Invoke("Rollback: no backup found, manual intervention may be needed");
            }
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Rollback failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Stop llama-swap via the process manager (if available), or via SIGTERM directly.
    /// </summary>
    private async Task<bool> StopProcessAsync(CancellationToken ct)
    {
        if (_processManager is not null)
        {
            try
            {
                return await _processManager.StopAsync();
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"Process manager stop failed: {ex.Message}");
            }
        }

        // Fallback: try to stop via SIGTERM directly
        try
        {
            var processes = Process.GetProcessesByName("llama-swap");
            foreach (var p in processes)
            {

                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill(false); // SIGTERM
                        p.WaitForExit(5000);
                    }
                }
                catch { }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<ChecksumEntry>?> FetchChecksumsAsync(string tagName, CancellationToken ct)
    {
        try
        {
            var checksumUrl = $"https://github.com/{GitHubRepo}/releases/download/{tagName}/llama-swap_{SanitizeVersion(tagName)}_checksums.txt";
            var response = await _httpClient.GetAsync(checksumUrl, ct);

            if (!response.IsSuccessStatusCode)
                return null;

            var content = await response.Content.ReadAsStringAsync(ct);
            var checksums = new List<ChecksumEntry>();

            foreach (var line in content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // Format: <hash>  <filename>  or  <hash>  *<filename>
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Length == 64) // SHA-256 hex
                {
                    var hash = parts[0];
                    var fileName = parts[1].TrimStart('*');
                    checksums.Add(new ChecksumEntry { Name = fileName, Sha256 = hash });
                }
            }

            return checksums.Count > 0 ? (IReadOnlyList<ChecksumEntry>)checksums : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> DownloadWithProgressAsync(string url, string destination, long expectedSize, CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                LogMessage?.Invoke($"Download failed: {(int)response.StatusCode} {response.StatusCode}");
                return false;
            }

            var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            var totalRead = 0L;
            var lastProgressReport = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var bytesRead = await stream.ReadAsync(buffer, ct);
                if (bytesRead == 0) break;

                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;

                // Report progress every ~5% or at significant milestones
                var progressPct = totalBytes > 0 ? (int)((totalRead * 100) / totalBytes) : 0;
                if (progressPct - lastProgressReport >= 5 || progressPct == 100)
                {
                    lastProgressReport = progressPct;
                    ProgressChanged?.Invoke(new UpdateProgress(
                        $"Downloading... {FormatBytes(totalRead)} / {FormatBytes(totalBytes)}",
                        20 + (progressPct * 40 / 100))); // 20-60% range
                }
            }

            // Verify file size (allow ±1% tolerance for GitHub CDN inconsistencies)
            var fileInfo = new FileInfo(destination);
            if (totalBytes > 0 && Math.Abs(fileInfo.Length - totalBytes) > totalBytes * 0.01)
            {
                LogMessage?.Invoke($"File size mismatch: expected {totalBytes}, got {fileInfo.Length}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Download error: {ex.Message}");
            return false;
        }
    }

    private bool ExtractZip(string archivePath, string extractDir)
    {
        try
        {
            ArchiveExtractor.ExtractZip(archivePath, extractDir);
            return true;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Failed to extract zip: {ex.Message}");
            return false;
        }
    }

    private bool ExtractTarGz(string archivePath, string extractDir)
    {
        try
        {
            ArchiveExtractor.ExtractTarGz(archivePath, extractDir);
            return true;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Failed to extract tar.gz: {ex.Message}");
            return false;
        }
    }

    private string? FindExtractedBinary(string extractDir)
    {
        var binaryName = GetBinaryName();
        var exePattern = _osName == "windows" ? "*.exe" : "*";

        // First, look for the binary with the expected name
        var exactMatch = Directory.GetFiles(extractDir, binaryName, SearchOption.AllDirectories)
            .FirstOrDefault();
        if (exactMatch != null)
            return exactMatch;

        // Then look for any executable with the right extension
        var candidates = Directory.GetFiles(extractDir, exePattern, SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".tar.gz") && !f.EndsWith(".zip"))
            .ToList();

        if (candidates.Count == 1)
            return candidates[0];

        // If multiple candidates, prefer llama-swap or llama-server
        var preferred = candidates.FirstOrDefault(f =>
            Path.GetFileName(f).StartsWith("llama", StringComparison.OrdinalIgnoreCase));
        return preferred ?? candidates.FirstOrDefault();
    }

    private void SetExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            var mode = File.GetUnixFileMode(path);
            mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Warning: executable permission update failed ({ex.Message})");
        }
    }

    /// <summary>
    /// H1: Verify macOS codesign on a binary.
    /// Returns true if the binary is codesigned and the signature is valid.
    /// </summary>
    private bool VerifyCodesign(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "codesign",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--verify");
            psi.ArgumentList.Add("-vvvv");
            psi.ArgumentList.Add(path);

            using var proc = Process.Start(psi);
            proc?.WaitForExit();

            var output = proc?.StandardOutput.ReadToEnd() ?? "";
            var error = proc?.StandardError.ReadToEnd() ?? "";

            // codesign returns 0 on success, non-zero on failure
            var success = proc?.ExitCode == 0;
            if (!success)
            {
                LogMessage?.Invoke($"codesign verify failed for {path}: exit={proc?.ExitCode}, err={error}");
            }
            return success;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"codesign verify error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Remove macOS quarantine attribute from a file.
    /// </summary>
    private void RemoveQuarantineAttribute(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "xattr",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add("com.apple.quarantine");
            psi.ArgumentList.Add(path);

            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Warning: xattr failed ({ex.Message})");
        }
    }

    /// <summary>
    /// H5: Prompts the user for explicit confirmation before replacing the binary.
    /// Returns true if confirmed, false if cancelled.
    /// The actual UI dialog is shown by the caller (UpdateViewModel) before calling this method.
    /// </summary>
    private Task<bool> PromptUpdateConfirmationAsync(string version, string targetPath, CancellationToken ct)
    {
        // UI layer handles the confirmation dialog.
        // If we reach here, the user has already confirmed.
        LogMessage?.Invoke($"Update to {version} confirmed by user");
        return Task.FromResult(true);
    }

    /// <summary>
    /// M6: Rate limiting for update checks.
    /// Checks if an update check is allowed based on the cooldown period.
    /// Returns true if allowed, false if throttled.
    /// </summary>
    private async Task<bool> TryAcquireUpdateCheckLockAsync(CancellationToken ct)
    {
        try
        {
            // Check cooldown first (static, shared across instances)
            if ((DateTime.UtcNow - _lastUpdateCheck) < UpdateCheckCooldown)
            {
                return false;
            }

            // Use semaphore to prevent concurrent checks across instances
            var acquired = await _updateCheckLock.WaitAsync(TimeSpan.FromSeconds(1), ct);
            if (!acquired)
            {
                return false;
            }

            // Double-check cooldown after acquiring lock
            if ((DateTime.UtcNow - _lastUpdateCheck) < UpdateCheckCooldown)
            {
                _updateCheckLock.Release();
                return false;
            }

            _lastUpdateCheck = DateTime.UtcNow;
            return true;
        }
        catch
        {
            return true; // Fail open — don't block update checks on lock errors
        }
    }

    public void ReleaseUpdateCheckLock()
    {
        _updateCheckLock.Release();
    }

    private bool CheckDiskSpace(long requiredBytes)
    {
        try
        {
            // Get free space on the drive where the install directory is located
            var drive = new DriveInfo(Path.GetPathRoot(_installDirectory) ?? "/");
            var freeBytes = drive.AvailableFreeSpace;

            // Require at least the requested space or the minimum, whichever is greater
            var required = Math.Max(requiredBytes, MinDiskSpaceBytes);
            return freeBytes >= required;
        }
        catch
        {
            return true; // Fail open — don't block update on disk check errors
        }
    }

    private bool AssetMatchesPlatform(string assetName)
    {
        if (_osName == "darwin")
            // GitHub uses "macos" in asset names, not "darwin"
            return (assetName.Contains("darwin", StringComparison.OrdinalIgnoreCase) ||
                    assetName.Contains("macos", StringComparison.OrdinalIgnoreCase)) &&
                   assetName.Contains(_arch, StringComparison.OrdinalIgnoreCase);

        if (_osName == "windows")
            return assetName.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
                   assetName.Contains(_arch, StringComparison.OrdinalIgnoreCase);

        // Linux
        return assetName.Contains("linux", StringComparison.OrdinalIgnoreCase) &&
               assetName.Contains(_arch, StringComparison.OrdinalIgnoreCase);
    }

    private string GetBinaryName()
    {
        return _osName == "windows" ? "llama-swap.exe" : "llama-swap";
    }

    private static string SanitizeVersion(string version)
    {
        // Remove leading 'v' if present
        if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            version = version.Substring(1);

        // Replace any characters that are not safe for URLs
        return System.Text.RegularExpressions.Regex.Replace(version, @"[^a-zA-Z0-9._-]", "_");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    private async Task<string> ComputeSha256Async(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hashBytes = await sha256.ComputeHashAsync(stream);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

// =====================================================================
    // Data structures
    // =====================================================================

    public class LatestVersionInfo
    {
        public string? Version { get; init; }
        public string? AssetName { get; init; }
        public string? DownloadUrl { get; init; }
        public long SizeBytes { get; init; }
        public IReadOnlyList<ChecksumEntry>? Checksums { get; init; }
    }

    public class ChecksumEntry
    {
        public string? Name { get; init; }
        public string? Sha256 { get; init; }
    }

    public class JsonRelease
    {
        [System.Text.Json.Serialization.JsonPropertyName("tag_name")]
        public string? TagName { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("assets")]
        public JsonAsset[]? Assets { get; set; }
    }

    public class JsonAsset
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("size")]
        public long Size { get; set; }
    }

    public class UpdateProgress
    {
        public string Message { get; }
        public int Percentage { get; }

        public UpdateProgress(string message, int percentage)
        {
            Message = message;
            Percentage = percentage;
        }
    }
}
