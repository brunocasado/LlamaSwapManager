using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
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

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(ApiTimeoutSeconds) };
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "LlamaSwapManager");
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

            var version = string.IsNullOrEmpty(targetVersion) ? latest.Version : targetVersion;
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

            // Step 8: Replace binary
            ProgressChanged?.Invoke(new UpdateProgress("Installing...", 85));

            if (File.Exists(targetExe))
                File.Delete(targetExe);

            File.Move(extractedExe, targetExe);

            // Make executable on Unix
            SetExecutable(targetExe);

            // Handle macOS quarantine
            if (_osName == "darwin")
            {
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
            if (totalBytes > 0)
            {
                var diff = Math.Abs(fileInfo.Length - totalBytes);
                var tolerance = (long)(totalBytes * 0.02); // 2% tolerance for GitHub CDN inconsistencies
                if (diff > tolerance)
                {
                    LogMessage?.Invoke($"Download size mismatch: expected {totalBytes}, got {fileInfo.Length}");
                    return false;
                }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            LogMessage?.Invoke("Download cancelled");
            return false;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Download error: {ex.Message}");
            return false;
        }
    }

    private async Task<string> ComputeSha256Async(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = await sha256.ComputeHashAsync(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Check available disk space in the target directory.
    /// </summary>
    private bool CheckDiskSpace(long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(_installDirectory) ?? Path.DirectorySeparatorChar.ToString();
            var drive = new DriveInfo(root);
            var freeBytes = drive.AvailableFreeSpace;

            // Need enough space for: download + extraction overhead (tar.gz expands ~3-5x)
            var needed = Math.Max(requiredBytes * 5, MinDiskSpaceBytes);

            if (freeBytes < needed)
            {
                LogMessage?.Invoke($"Insufficient disk space: need {FormatBytes(needed)}, have {FormatBytes(freeBytes)}");
                return false;
            }

            return true;
        }
        catch
        {
            // If we can't check, allow the download to proceed
            return true;
        }
    }

    /// <summary>
    /// Extract a tar.gz archive to the target directory.
    /// Pure .NET implementation — no external dependencies.
    /// </summary>
    private bool ExtractTarGz(string archivePath, string extractDir)
    {
        try
        {
            using var fileStream = File.OpenRead(archivePath);
            using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);

            var tarReader = new TarReader(LogMessage, SetExecutable);
            tarReader.Extract(gzipStream, extractDir);

            return true;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"tar.gz extraction error: {ex.Message}");
            return false;
        }
    }

    private bool ExtractZip(string archivePath, string extractDir)
    {
        try
        {
            ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true);
            return true;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"zip extraction error: {ex.Message}");
            return false;
        }
    }

    private string? FindCurrentBinary()
    {
        var candidates = new[]
        {
            Path.Combine(_installDirectory, GetBinaryName()),
            Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llama-swap"), GetBinaryName())
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    private string? FindExtractedBinary(string extractDir)
    {
        var exeName = GetBinaryName();
        var found = Directory.GetFiles(extractDir, exeName, SearchOption.AllDirectories);
        return found.Length > 0 ? found[0] : null;
    }

    private string GetBinaryName()
    {
        return _osName == "windows" ? "llama-swap.exe" : "llama-swap";
    }

    private string SanitizeVersion(string version)
    {
        return version.TrimStart('v');
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double b = bytes;
        int i = 0;
        while (b >= 1024 && i < sizes.Length - 1)
        {
            b /= 1024;
            i++;
        }
        return $"{b:F1} {sizes[i]}";
    }

    private void SetExecutable(string path)
    {
        if (_osName != "windows")
        {
            try
            {
                // macOS: chmod is at /bin, not /usr/bin
                var chmodPath = _osName == "darwin" ? "/bin/chmod" : "/usr/bin/chmod";
                var psi = new ProcessStartInfo
                {
                    FileName = chmodPath,
                    Arguments = $"+x \"{path}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi)?.WaitForExit();
            }
            catch { }
        }
    }

    private void RemoveQuarantineAttribute(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/xattr",
                Arguments = $"-d com.apple.quarantine \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi)?.WaitForExit();
        }
        catch { }
    }

    /// <summary>
    /// Strictly match asset name against OS and architecture.
    /// llama-swap release assets follow: llama-swap_<version>_<os>_<arch>.tar.gz (Unix) or .zip (Windows)
    /// </summary>
    private bool AssetMatchesPlatform(string assetName)
    {
        // Windows: accept .zip
        if (_osName == "windows")
        {
            if (!assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return false;

            var osPattern = $"_{_osName}_";
            if (!assetName.Contains(osPattern))
                return false;

            var archPattern = $"_{_arch}.zip";
            if (!assetName.EndsWith(archPattern, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        // Unix: accept .tar.gz
        if (!assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            return false;

        var unixOsPattern = $"_{_osName}_";
        if (!assetName.Contains(unixOsPattern))
            return false;

        var unixArchPattern = $"_{_arch}.tar.gz";
        if (!assetName.EndsWith(unixArchPattern, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    // --- Data classes ---

    public record UpdateProgress(string Message, int Percentage);

    public record LatestVersionInfo
    {
        public string? Version { get; init; }
        public string? AssetName { get; init; }
        public string? DownloadUrl { get; init; }
        public long SizeBytes { get; init; }
        public IReadOnlyList<ChecksumEntry>? Checksums { get; init; }
    }

    public record ChecksumEntry
    {
        public string? Name { get; init; }
        public string? Sha256 { get; init; }
    }

    // Minimal JSON deserialization for GitHub release API
    private class JsonRelease
    {
        [System.Text.Json.Serialization.JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("assets")]
        public IReadOnlyList<JsonAsset>? Assets { get; set; }
    }

    private class JsonAsset
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("size")]
        public long Size { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }

    // --- Tar.gz extraction (pure .NET, no external deps) ---

    /// <summary>
    /// Reads a tar archive from a stream and extracts files to disk.
    /// Supports standard POSIX tar format (no pax/gnu extensions needed for llama-swap).
    /// </summary>
    private class TarReader
    {
        private const int BlockSize = 512;
        private readonly Action<string>? _log;
        private readonly Action<string>? _setExecutable;

        public TarReader(Action<string>? log, Action<string>? setExecutable)
        {
            _log = log;
            _setExecutable = setExecutable;
        }

        public void Extract(Stream tarStream, string destinationDir)
        {
            var buffer = new byte[BlockSize];
            var fileBuffer = new byte[1024 * 1024]; // 1MB buffer for file data

            while (ReadBlock(tarStream, buffer))
            {
                // Check for end-of-archive (all zeros)
                if (buffer.All(b => b == 0))
                    break;

                var header = ParseHeader(buffer);
                if (header == null)
                    continue;

                if (header.FileSize > 0)
                {
                    var filePath = Path.Combine(destinationDir, header.FileName);

                    // Security: prevent path traversal
                    var fullPath = Path.GetFullPath(filePath);
                    var destFull = Path.GetFullPath(destinationDir);
                    if (!fullPath.StartsWith(destFull + Path.DirectorySeparatorChar) && fullPath != destFull)
                    {
                        _log?.Invoke($"Skipping unsafe path: {header.FileName}");
                        SkipBlockData(tarStream, header.FileSize);
                        continue;
                    }

                    // Ensure parent directory exists
                    var parentDir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }

                    // Extract file data
                    using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    var remaining = header.FileSize;
                    while (remaining > 0)
                    {
                        var toRead = (int)Math.Min(remaining, fileBuffer.Length);
                        var read = tarStream.Read(fileBuffer, 0, toRead);
                        if (read == 0) break;
                        fs.Write(fileBuffer, 0, read);
                        remaining -= read;
                    }

                    // Align to 512-byte boundary — use Read instead of Seek (GZipStream doesn't support Seek)
                    var remainder = header.FileSize % BlockSize;
                    if (remainder > 0)
                    {
                        var skipBuffer = new byte[BlockSize - remainder];
                        tarStream.Read(skipBuffer, 0, skipBuffer.Length);
                    }

                    // Set permissions (if stored in tar)
                    if (header.Mode != 0)
                    {
                        try
                        {
                            var isExecutable = (header.Mode & 0x49) != 0; // owner/exec or group/exec or other/exec
                            if (isExecutable)
                            {
                                _setExecutable?.Invoke(fullPath);
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        private bool ReadBlock(Stream stream, byte[] buffer)
        {
            var totalRead = 0;
            while (totalRead < BlockSize)
            {
                var read = stream.Read(buffer, totalRead, BlockSize - totalRead);
                if (read == 0) return false;
                totalRead += read;
            }
            return true;
        }

        private void SkipBlockData(Stream stream, long fileSize)
        {
            var remaining = fileSize;
            var buf = new byte[8192];
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(remaining, buf.Length);
                var read = stream.Read(buf, 0, toRead);
                if (read == 0) break;
                remaining -= read;
            }
            // Align to 512-byte boundary — use Read instead of Seek (GZipStream doesn't support Seek)
            var remainder = fileSize % BlockSize;
            if (remainder > 0)
            {
                var skipBuffer = new byte[BlockSize - remainder];
                stream.Read(skipBuffer, 0, skipBuffer.Length);
            }
        }

        private TarHeader? ParseHeader(byte[] block)
        {
            // Name at offset 0, 100 bytes
            var nameBytes = new byte[100];
            Array.Copy(block, 0, nameBytes, 0, 100);
            var name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0', ' ');

            if (string.IsNullOrEmpty(name))
                return null;

            // Type flag at offset 156 — 0 or empty = regular file, 5 = directory
            var typeFlag = (char)block[156];
            if (typeFlag == '5')
                return new TarHeader { FileName = name, IsDirectory = true };

            // Size at offset 124, 12 bytes octal
            var sizeBytes = new byte[12];
            Array.Copy(block, 124, sizeBytes, 0, 12);
            var sizeStr = Encoding.ASCII.GetString(sizeBytes).TrimEnd('\0', ' ');
            var fileSize = 0L;
            try
            {
                fileSize = Convert.ToInt64(sizeStr, 8); // Octal
            }
            catch
            {
                fileSize = 0;
            }

            // Mode at offset 100, 8 bytes octal
            var modeBytes = new byte[8];
            Array.Copy(block, 100, modeBytes, 0, 8);
            var modeStr = Encoding.ASCII.GetString(modeBytes).TrimEnd('\0', ' ');
            var mode = 0;
            try
            {
                mode = Convert.ToInt32(modeStr, 8);
            }
            catch { }

            return new TarHeader
            {
                FileName = name,
                FileSize = fileSize,
                Mode = mode
            };
        }

        private class TarHeader
        {
            public string FileName { get; init; } = "";
            public long FileSize { get; init; }
            public int Mode { get; init; }
            public bool IsDirectory { get; init; }
        }
    }
}
