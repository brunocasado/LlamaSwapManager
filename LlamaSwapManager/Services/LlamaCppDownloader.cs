using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

/// <summary>
/// Downloads and installs llama.cpp binaries from GitHub releases.
/// Handles platform detection, checksum verification, extraction, backup, and rollback.
/// </summary>
public class LlamaCppDownloader : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _userDirectory;
    private readonly string _downloadsDir;
    private const string GithubApiBase = "https://api.github.com/repos/ggml-org/llama.cpp";

    public event Action<string>? LogMessage;

    public LlamaCppDownloader(string? userDirectory = null)
    {
        _userDirectory = userDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llama-swap");
        _downloadsDir = Path.Combine(_userDirectory, ".updates");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LlamaSwapManager");
    }

    /// <summary>
    /// Download and install the latest llama.cpp release to the given directory.
    /// </summary>
    /// <param name="targetDirectory">Directory where llama.cpp binaries should be installed (e.g. ~/.llama/).</param>
    /// <param name="progress">Progress reporter (0.0 to 1.0).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if install succeeded, false otherwise.</returns>
    public async Task<bool> DownloadAndInstallAsync(
        string targetDirectory,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(0.0);
        LogMessage?.Invoke("[llama.cpp] Starting download of latest release...");

        // Step 1: Get latest release info from GitHub API
        progress?.Report(0.05);
        var release = await GetLatestReleaseAsync(ct);
        if (release == null)
        {
            LogMessage?.Invoke("[llama.cpp] Failed to fetch latest release info");
            return false;
        }

        var tag = release?.GetProperty("tag_name").GetString() ?? "";
        LogMessage?.Invoke($"[llama.cpp] Latest version: {tag}");

        // Step 2: Detect the right asset for current platform (includes CUDA version-aware selection)
        progress?.Report(0.15);
        var assetInfo = DetectAssetForPlatform(tag, release);
        if (assetInfo == null)
        {
            LogMessage?.Invoke("[llama.cpp] No suitable asset found for this platform");
            return false;
        }

        LogMessage?.Invoke($"[llama.cpp] Selected asset: {assetInfo.Name} ({FormatSize(assetInfo.Size)})");

        // Step 3: Create temp download directory
        var tempDir = CreateTempDirectory(tag);
        var archivePath = Path.Combine(tempDir, assetInfo.Name);

        // Step 4: Download the archive
        progress?.Report(0.20);
        var downloadOk = await DownloadAssetAsync(assetInfo.Url, archivePath, progress, ct);
        if (!downloadOk || ct.IsCancellationRequested)
        {
            LogMessage?.Invoke("[llama.cpp] Download failed or cancelled");
            DeleteDirectory(tempDir);
            return false;
        }

        // Step 5: Verify SHA-256 checksum
        progress?.Report(0.70);
        var checksumOk = await VerifyChecksumAsync(archivePath, assetInfo.Digest, ct);
        if (!checksumOk)
        {
            LogMessage?.Invoke("[llama.cpp] Checksum verification failed — aborting");
            DeleteDirectory(tempDir);
            return false;
        }

        LogMessage?.Invoke("[llama.cpp] Checksum verified OK");

        // Step 6: Extract and install with backup/rollback
        progress?.Report(0.80);
        var installOk = await ExtractAndInstallAsync(tempDir, archivePath, targetDirectory, ct);

        // Clean up temp directory
        DeleteDirectory(tempDir);

        // Step 7: Download CUDA runtime libraries (non-critical, only for CUDA backend)
        if (installOk && IsCudaBackend() && assetInfo.CudartAssets.Any())
        {
            progress?.Report(0.90);
            await DownloadCudaRuntimeAsync(assetInfo.CudartAssets.FirstOrDefault(), targetDirectory, ct);
        }

        if (installOk)
        {
            LogMessage?.Invoke($"[llama.cpp] Install complete — version {tag} in {targetDirectory}");
        }
        else
        {
            LogMessage?.Invoke("[llama.cpp] Install failed — check logs for details");
        }

        progress?.Report(1.0);
        return installOk;
    }

    /// <summary>
    /// Check if an update is available by comparing the local version against the latest remote version.
    /// </summary>
    public async Task<(bool HasUpdate, string? RemoteVersion, string? LocalVersion)> CheckForUpdateAsync(
        string targetDirectory,
        CancellationToken ct = default)
    {
         var release = await GetLatestReleaseAsync(ct);
        var remoteVersion = release?.GetProperty("tag_name").GetString();

        var localVersion = DetectLocalVersion(targetDirectory);

        return (
            HasUpdate: remoteVersion != null && !string.Equals(remoteVersion, localVersion, StringComparison.Ordinal),
            RemoteVersion: remoteVersion,
            LocalVersion: localVersion
        );
    }

    // ---- GitHub API ----

    private async Task<JsonElement?> GetLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync($"{GithubApiBase}/releases/latest", ct);
            if (!response.IsSuccessStatusCode)
            {
                LogMessage?.Invoke($"[llama.cpp] GitHub API error: {(int)response.StatusCode} {response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            // Clone the root element so it survives after the JsonDocument is disposed.
            // doc.RootElement is a struct referencing internal memory — it becomes invalid
            // once the using block exits.
            var clone = doc.RootElement.Clone();
            return clone;
        }
        catch (HttpRequestException ex)
        {
            LogMessage?.Invoke($"[llama.cpp] Network error fetching release: {ex.Message}");
            return null;
        }
        catch (TaskCanceledException)
        {
            LogMessage?.Invoke("[llama.cpp] Request timed out");
            return null;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[llama.cpp] Error fetching release: {ex.Message}");
            return null;
        }
    }

    // ---- Asset Detection ----

    private DetectedAsset? DetectAssetForPlatform(
        string tag, JsonElement? release)
    {
        // macOS: always use platform-specific build (Metal built-in)
        var isMacArm = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
                       RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        var isMacIntel = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
                         RuntimeInformation.ProcessArchitecture == Architecture.X64;

        var patterns = new List<string?>();
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        if (isMacArm)
        {
            patterns.Add("-macos-arm64-");
        }
        else if (isMacIntel)
        {
            patterns.Add("-macos-x64-");
        }
        else if (isWindows || isLinux)
        {
            // Windows/Linux: use user preference or auto-detect
            var effectiveBackend = GpuDetectionSettings.GetEffectiveBackend();
            var userPattern = GpuDetectionService.GetPreferredAssetPattern(effectiveBackend);
            if (userPattern != null)
                patterns.Add(userPattern);

            // Fall back to auto-detected backends in priority order
            var detected = GpuDetectionService.DetectBackends();
            foreach (var gpu in detected)
            {
                if (gpu.Backend == effectiveBackend) continue;
                var pattern = GpuDetectionService.GetPreferredAssetPattern(gpu.Backend);
                if (pattern != null && !patterns.Contains(pattern))
                    patterns.Add(pattern);
            }
        }

        if (release == null)
        {
            LogMessage?.Invoke("[llama.cpp] No release info found");
            return null;
        }

        if (!release.Value.TryGetProperty("assets", out var assets))
        {
            LogMessage?.Invoke("[llama.cpp] No assets property found in release");
            return null;
        }

        var assetArray = assets.EnumerateArray().ToList();
        if (!assetArray.Any())
        {
            LogMessage?.Invoke("[llama.cpp] No assets found in release");
            return null;
        }

        // Parse CUDA assets for version-aware selection
        var allCudaAssets = ParseCudaAssets(release.Value);
        var cudartAssets = allCudaAssets.Where(a => a.AssetType == CudaAssetType.Cudart).ToList();

        // For CUDA backend on Windows/Linux, use version-aware asset selection
        if (isWindows || isLinux)
        {
            var effectiveBackend = GpuDetectionSettings.GetEffectiveBackend();

            if (effectiveBackend == GpuDetectionService.GpuBackend.Cuda)
            {
                var cudaVersion = CudaVersionDetector.GetCudaVersion();
                var llamaCudaAssets = allCudaAssets.Where(a => a.AssetType == CudaAssetType.LlamaBuild).ToList();

                if (llamaCudaAssets.Any() && !string.IsNullOrEmpty(cudaVersion))
                {
                    // Version-aware selection for CUDA builds
                    var bestAsset = FindBestCudaAsset(llamaCudaAssets, cudaVersion);
                    if (bestAsset != null)
                    {
                        LogMessage?.Invoke($"[llama.cpp] CUDA version-aware selection: {bestAsset.Name}");
                        return new DetectedAsset(
                            bestAsset.Name,
                            bestAsset.Size,
                            bestAsset.Url,
                            bestAsset.Digest,
                            cudartAssets);
                    }
                }
            }
        }

        // Non-CUDA path: use existing pattern-based detection
        var arch = RuntimeInformation.ProcessArchitecture;
        var archFilter = arch == Architecture.Arm64 ? "arm64" : "x64";

        // Try each pattern in priority order
        foreach (var pattern in patterns)
        {
            if (pattern == null) continue;

            foreach (var asset in assetArray)
            {
                var name = asset.GetProperty("name").GetString() ?? "";

                // Match pattern + architecture + correct archive format
                if (name.Contains(pattern) &&
                    name.EndsWith(".tar.gz") &&
                    name.Contains(archFilter))
                {
                    return new DetectedAsset(
                        name, asset.GetProperty("size").GetInt64(),
                        asset.GetProperty("browser_download_url").GetString() ?? "",
                        asset.GetProperty("digest").GetString() ?? "",
                        cudartAssets);
                }
            }
        }

        // Final fallback: any platform-appropriate tar.gz/zip
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            foreach (var asset in assetArray)
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.Contains("-macos-") && name.EndsWith(".tar.gz") && !name.Contains("xcframework"))
                {
                    return new DetectedAsset(
                        name, asset.GetProperty("size").GetInt64(),
                        asset.GetProperty("browser_download_url").GetString() ?? "",
                        asset.GetProperty("digest").GetString() ?? "",
                        cudartAssets);
                }
            }
        }
        else if (isWindows)
        {
            foreach (var asset in assetArray)
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.Contains("-win-cpu-x64-") && name.EndsWith(".zip"))
                {
                    return new DetectedAsset(
                        name, asset.GetProperty("size").GetInt64(),
                        asset.GetProperty("browser_download_url").GetString() ?? "",
                        asset.GetProperty("digest").GetString() ?? "",
                        cudartAssets);
                }
            }
        }
        else if (isLinux)
        {
            foreach (var asset in assetArray)
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.Contains("-ubuntu-x64-") && name.EndsWith(".tar.gz"))
                {
                    return new DetectedAsset(
                        name, asset.GetProperty("size").GetInt64(),
                        asset.GetProperty("browser_download_url").GetString() ?? "",
                        asset.GetProperty("digest").GetString() ?? "",
                        cudartAssets);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Check if the current backend is CUDA-based.
    /// </summary>
    private static bool IsCudaBackend()
    {
        return GpuDetectionSettings.GetEffectiveBackend() == GpuDetectionService.GpuBackend.Cuda;
    }

    // ---- Download ----

    private async Task<bool> DownloadAssetAsync(
        string url, string destinationPath, IProgress<double>? progress, CancellationToken ct)
    {
        try
        {
            // Check disk space before downloading
            var parentDir = Directory.GetParent(destinationPath);
            if (parentDir != null && !HasEnoughDiskSpace(parentDir.FullName, 500 * 1024 * 1024))
            {
                LogMessage?.Invoke("[llama.cpp] Insufficient disk space (need 500MB free)");
                return false;
            }

            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                LogMessage?.Invoke($"[llama.cpp] Download error: {(int)response.StatusCode} {response.StatusCode}");
                return false;
            }

            var contentLength = response.Content.Headers.ContentLength;
            var totalBytes = contentLength ?? 0;

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            var totalRead = 0L;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;

                if (totalBytes > 0 && progress != null)
                {
                    var downloadProgress = 0.20 + (0.50 * (double)totalRead / totalBytes);
                    progress.Report(Math.Min(downloadProgress, 0.70));
                }
            }

            return true;
        }
        catch (TaskCanceledException)
        {
            LogMessage?.Invoke("[llama.cpp] Download cancelled or timed out");
            return false;
        }
        catch (HttpRequestException ex)
        {
            LogMessage?.Invoke($"[llama.cpp] Download network error: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[llama.cpp] Download error: {ex.Message}");
            return false;
        }
    }

    // ---- Checksum Verification ----

    private async Task<bool> VerifyChecksumAsync(string filePath, string? expectedDigest, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(expectedDigest) || !expectedDigest.StartsWith("sha256:"))
        {
            LogMessage?.Invoke("[llama.cpp] No checksum available — skipping verification (WARNING)");
            // Don't fail on missing checksum — just warn
            return true;
        }

        var expectedHash = expectedDigest.Substring("sha256:".Length).ToLowerInvariant();

        try
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = await sha256.ComputeHashAsync(stream, ct);
            var actualHash = BitConverter.ToString(hashBytes).ToLowerInvariant().Replace("-", "");

            if (actualHash != expectedHash)
            {
                LogMessage?.Invoke($"[llama.cpp] Checksum mismatch: expected {expectedHash}, got {actualHash}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[llama.cpp] Checksum verification error: {ex.Message}");
            return false;
        }
    }

    // ---- Extract & Install with Backup/Rollback ----

    private async Task<bool> ExtractAndInstallAsync(
        string tempDir, string archivePath, string targetDirectory, CancellationToken ct)
    {
        // Ensure target directory exists
        if (!Directory.Exists(targetDirectory))
        {
            try
            {
                Directory.CreateDirectory(targetDirectory);
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[llama.cpp] Cannot create target directory: {ex.Message}");
                return false;
            }
        }

        // Backup existing files in target directory
        var backupPath = Path.Combine(_downloadsDir, $"llama-cpp-backup-{DateTime.UtcNow:yyyyMMddHHmmss}");
        try
        {
            LogMessage?.Invoke("[llama.cpp] Backing up existing llama.cpp files...");
            CopyDirectory(targetDirectory, backupPath);

            // Extract archive to a staging directory inside temp
            var stagingDir = Path.Combine(tempDir, "staging");
            Directory.CreateDirectory(stagingDir);

            LogMessage?.Invoke("[llama.cpp] Extracting archive...");

            if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await ExtractZipAsync(archivePath, stagingDir, ct);
            }
            else if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                     archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                await ExtractTarGzAsync(archivePath, stagingDir, ct);
            }
            else
            {
                LogMessage?.Invoke("[llama.cpp] Unsupported archive format");
                Rollback(backupPath, targetDirectory);
                return false;
            }

            // Copy extracted files to target directory
            LogMessage?.Invoke("[llama.cpp] Installing new files...");
            CopyDirectoryContents(stagingDir, targetDirectory);

            // Make binaries executable on Unix
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                MakeBinariesExecutable(targetDirectory);
            }

            // Verify key binary exists after install
            if (!File.Exists(Path.Combine(targetDirectory, "llama-server")))
            {
                LogMessage?.Invoke("[llama.cpp] Installed binary 'llama-server' not found — rollback");
                Rollback(backupPath, targetDirectory);
                return false;
            }

            // Remove macOS quarantine attribute
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "xattr",
                        Arguments = $"-d com.apple.quarantine \"{targetDirectory}\"",
                        UseShellExecute = true,
                        CreateNoWindow = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    proc?.WaitForExit();
                }
                catch { /* non-critical */ }
            }

            // Success — remove backup
            DeleteDirectory(backupPath);
            return true;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[llama.cpp] Install error: {ex.Message}");
            Rollback(backupPath, targetDirectory);
            return false;
        }
    }

    private async Task ExtractZipAsync(string archivePath, string destinationDir, CancellationToken ct)
    {
        // Handle zip files that may have a top-level directory
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries.ToList();

        // Check if all entries share a common top-level directory
        var topDir = GetTopLevelDirectory(entries);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            // Skip empty directories
            if (entry.Length == 0 && string.IsNullOrEmpty(entry.Name))
                continue;

            // Remove top-level directory prefix if present
            var targetPath = topDir != null && entry.FullName.StartsWith(topDir)
                ? entry.FullName.Substring(topDir.Length).TrimStart('/', '\\')
                : entry.FullName;

            if (string.IsNullOrEmpty(targetPath))
                continue;

            var fullPath = Path.Combine(destinationDir, targetPath);
            var fullDir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(fullDir) && !Directory.Exists(fullDir))
                Directory.CreateDirectory(fullDir);

            entry.ExtractToFile(fullPath, overwrite: true);
        }
    }

    private async Task ExtractTarGzAsync(string archivePath, string destinationDir, CancellationToken ct)
    {
        // Handle tar.gz files that may have a top-level directory
        using var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        using var tarArchive = new TarArchive(gzip);

        var entries = tarArchive.Entries.ToList();
        var topDir = GetTopLevelDirectoryFromTar(entries);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(entry.Name))
                continue;

            // Remove top-level directory prefix if present
            var targetPath = topDir != null && entry.Name.StartsWith(topDir)
                ? entry.Name.Substring(topDir.Length).TrimStart('/')
                : entry.Name;

            if (string.IsNullOrEmpty(targetPath))
                continue;

            var fullPath = Path.Combine(destinationDir, targetPath);

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(fullPath);
            }
            else
            {
                var fullDir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(fullDir) && !Directory.Exists(fullDir))
                    Directory.CreateDirectory(fullDir);

                using var entryStream = entry.DataStream;
                using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await entryStream.CopyToAsync(fileStream, 81920, ct);
            }
        }
    }

    private string? GetTopLevelDirectory(System.Collections.Generic.IList<ZipArchiveEntry> entries)
    {
        if (entries.Count == 0) return null;

        // Find the common prefix of all non-empty entry paths
        var firstPath = entries[0].FullName;
        var firstSlash = firstPath.IndexOf('/');
        var firstBackslash = firstPath.IndexOf('\\');
        var firstSlashIdx = Math.Max(firstSlash, firstBackslash);

        if (firstSlashIdx <= 0) return null; // No top-level directory

        var candidate = firstPath.Substring(0, firstSlashIdx + 1);

        // Verify all entries start with this prefix
        foreach (var entry in entries)
        {
            if (!entry.FullName.StartsWith(candidate, StringComparison.Ordinal) && !string.IsNullOrEmpty(entry.FullName))
            {
                return null;
            }
        }

        return candidate;
    }

    private string? GetTopLevelDirectoryFromTar(System.Collections.Generic.IList<TarEntry> entries)
    {
        if (entries.Count == 0) return null;

        var firstPath = entries[0].Name;
        var firstSlash = firstPath.IndexOf('/');

        if (firstSlash <= 0) return null;

        var candidate = firstPath.Substring(0, firstSlash + 1);

        foreach (var entry in entries)
        {
            if (!string.IsNullOrEmpty(entry.Name) && !entry.Name.StartsWith(candidate, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return candidate;
    }

    private void CopyDirectoryContents(string sourceDir, string targetDir)
    {
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }

        foreach (var subdir in Directory.GetDirectories(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(subdir));
            if (!Directory.Exists(dest))
                Directory.CreateDirectory(dest);
            CopyDirectoryContents(subdir, dest);
        }
    }

    private void MakeBinariesExecutable(string directory)
    {
        foreach (var file in Directory.GetFiles(directory))
        {
            var name = Path.GetFileName(file);
            // Make known binary names executable
            if (name.StartsWith("llama") || name.StartsWith("ggml") || name == "rpc-server")
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "chmod",
                        Arguments = "+x " + ShellQuote(file),
                        UseShellExecute = true,
                        CreateNoWindow = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    proc?.WaitForExit();
                }
                catch { /* non-critical */ }
            }
        }
    }

    private void Rollback(string backupPath, string targetDirectory)
    {
        LogMessage?.Invoke("[llama.cpp] Rolling back to backup...");

        try
        {
            // Clear target directory
            if (Directory.Exists(targetDirectory))
            {
                foreach (var file in Directory.GetFiles(targetDirectory))
                    File.Delete(file);
                foreach (var subdir in Directory.GetDirectories(targetDirectory))
                    DeleteDirectory(subdir);
            }

            // Restore from backup
            if (Directory.Exists(backupPath))
            {
                CopyDirectoryContents(backupPath, targetDirectory);
                LogMessage?.Invoke("[llama.cpp] Rollback complete");
            }
            else
            {
                LogMessage?.Invoke("[llama.cpp] Rollback failed — backup not found");
            }
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[llama.cpp] Rollback error: {ex.Message}");
        }
    }

    // ---- Local Version Detection ----

    private string? DetectLocalVersion(string targetDirectory)
    {
        // Try to find llama-server binary and check version
        var serverPath = Path.Combine(targetDirectory, "llama-server");
        if (!File.Exists(serverPath))
        {
            // Try with .exe suffix on Windows
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                serverPath = serverPath + ".exe";
                if (!File.Exists(serverPath)) return null;
            }
            else
            {
                return null;
            }
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = serverPath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(5000);
            var output = proc?.StandardOutput.ReadToEnd() ?? "";

            // Try to parse version from output (format varies by build)
            // Common patterns: "llama-server version X" or "llama.cpp version bXXXX"
            var match = System.Text.RegularExpressions.Regex.Match(output, @"(?:version\s+)?(b[0-9a-fA-F]{5})");
            if (match.Success)
                return match.Groups[1].Value;

            return null;
        }
        catch
        {
            return null;
        }
    }

    // ---- Helpers ----

    private string CreateTempDirectory(string tag)
    {
        Directory.CreateDirectory(_downloadsDir);
        var dirName = $"llama-cpp-{tag}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var dir = Path.Combine(_downloadsDir, dirName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void CopyDirectory(string source, string target)
    {
        if (!Directory.Exists(source)) return;

        if (!Directory.Exists(target))
            Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var subdir in Directory.GetDirectories(source))
        {
            CopyDirectory(subdir, Path.Combine(target, Path.GetFileName(subdir)));
        }
    }

    private void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { /* best effort cleanup */ }
    }

    private static bool HasEnoughDiskSpace(string path, long requiredBytes)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var drive = DriveInfo.GetDrives().FirstOrDefault(d => path.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase));
                return drive?.AvailableFreeSpace > requiredBytes;
            }
            else
            {
                // Use statvfs on Unix
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "stat",
                    Arguments = $"-f %a*%s {path}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(3000);
                var output = proc?.StandardOutput.ReadToEnd()?.Trim();
                if (!string.IsNullOrEmpty(output))
                {
                    var parts = output.Split(' ');
                    if (parts.Length == 2 && long.TryParse(parts[0], out var freeBlocks) && long.TryParse(parts[1], out var blockSize))
                    {
                        return (freeBlocks * blockSize) > requiredBytes;
                    }
                }
            }
        }
        catch { }

        // If we can't check, assume there's enough space (safer to proceed)
        return true;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    private static string ShellQuote(string s)
    {
        return "'" + s.Replace("'", "'\\''") + "'";
    }

    public void Dispose()
    {
        _http?.Dispose();
    }

    // ---- CUDA Version-Aware Asset Selection ----

    /// <summary>
    /// Represents a CUDA-related asset parsed from a GitHub release.
    /// </summary>
    internal record CudaAsset(
        string Name,
        string Url,
        long Size,
        string Digest,
        CudaAssetType AssetType,
        string CudaVersion);

    /// <summary>
    /// Type of CUDA asset: llama build or cudart runtime.
    /// </summary>
    internal enum CudaAssetType
    {
        LlamaBuild,
        Cudart
    }

    /// <summary>
    /// Result of asset detection, including the main asset and any associated cudart assets.
    /// </summary>
    internal record DetectedAsset(
        string Name,
        long Size,
        string Url,
        string Digest,
        IReadOnlyList<CudaAsset> CudartAssets);

    /// <summary>
    /// Regex to parse CUDA version from asset names like "llama-cuda12.4-win-cuda-12.4-x64-release.tar.gz".
    /// Captures major.minor (and optional patch) as group 1.
    /// </summary>
    private static readonly Regex CudaAssetVersionRegex = new(
        @"cuda(\d+\.\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Regex to match cudart runtime assets.
    /// Matches patterns like "cudart-cuda12.4-win-12.4-x64.tar.gz" or "cudart-cuda-12.4-ubuntu-x64.tar.gz".
    /// </summary>
    private static readonly Regex CudartAssetRegex = new(
        @"cudart-cuda[\-_](\d+\.\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Regex to match llama CUDA build assets (not cudart).
    /// Matches patterns like "llama-cuda12.4-win-cuda-12.4-x64-release.tar.gz".
    /// </summary>
    private static readonly Regex LlamaCudaBuildRegex = new(
        @"llama-cuda\d+.*?cuda-\d+\.\d+(?:\.\d+)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parse CUDA-related assets from a GitHub release.
    /// Returns both llama CUDA builds and cudart runtime assets.
    /// </summary>
    private List<CudaAsset> ParseCudaAssets(JsonElement release)
    {
        var cudaAssets = new List<CudaAsset>();

        if (!release.TryGetProperty("assets", out var assets))
            return cudaAssets;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";

            // Check for cudart runtime assets
            var cudartMatch = CudartAssetRegex.Match(name);
            if (cudartMatch.Success)
            {
                var cudaVersion = cudartMatch.Groups[1].Value;
                cudaAssets.Add(new CudaAsset(
                    name,
                    asset.GetProperty("browser_download_url").GetString() ?? "",
                    asset.GetProperty("size").GetInt64(),
                    asset.GetProperty("digest").GetString() ?? "",
                    CudaAssetType.Cudart,
                    cudaVersion));
                continue;
            }

            // Check for llama CUDA build assets
            if (LlamaCudaBuildRegex.IsMatch(name) ||
                (name.Contains("cuda", StringComparison.OrdinalIgnoreCase) &&
                 name.Contains("llama", StringComparison.OrdinalIgnoreCase)))
            {
                var versionMatch = CudaAssetVersionRegex.Match(name);
                if (versionMatch.Success)
                {
                    var cudaVersion = versionMatch.Groups[1].Value;
                    cudaAssets.Add(new CudaAsset(
                        name,
                        asset.GetProperty("browser_download_url").GetString() ?? "",
                        asset.GetProperty("size").GetInt64(),
                        asset.GetProperty("digest").GetString() ?? "",
                        CudaAssetType.LlamaBuild,
                        cudaVersion));
                }
            }
        }

        return cudaAssets;
    }

    /// <summary>
    /// Find the best CUDA asset matching the installed CUDA version.
    /// Priority: exact match → same major version → newest available.
    /// </summary>
    private CudaAsset? FindBestCudaAsset(List<CudaAsset> cudaAssets, string installedCudaVersion)
    {
        if (!cudaAssets.Any())
            return null;

        var installedParts = installedCudaVersion.Split('.');
        var installedMajor = installedParts[0];
        var installedMinor = installedParts.Length > 1 ? installedParts[1] : "0";

        // 1. Exact match
        var exactMatch = cudaAssets.FirstOrDefault(a => a.CudaVersion == installedCudaVersion);
        if (exactMatch != null)
        {
            LogMessage?.Invoke($"[llama.cpp] Found exact CUDA {installedCudaVersion} asset: {exactMatch.Name}");
            return exactMatch;
        }

        // 2. Major version match (e.g. CUDA 12.6 → 12.4)
        var majorMatches = cudaAssets
            .Where(a => a.CudaVersion.Split('.')[0] == installedMajor)
            .ToList();

        if (majorMatches.Any())
        {
            // Pick the highest minor version among major matches
            var best = majorMatches.OrderByDescending(a => ParseVersionComponents(a.CudaVersion)).ToList()[0];
            LogMessage?.Invoke($"[llama.cpp] No exact CUDA match for {installedCudaVersion}, using CUDA {best.CudaVersion} asset: {best.Name}");
            return best;
        }

        // 3. Fallback: newest available
        var newest = cudaAssets.OrderByDescending(a => ParseVersionComponents(a.CudaVersion)).First();
        LogMessage?.Invoke($"[llama.cpp] No CUDA {installedMajor}.x match, using newest available: {newest.Name}");
        return newest;
    }

    /// <summary>
    /// Parse version components for comparison. Returns (major * 1000 + minor, patch).
    /// </summary>
    private static (int majorMinor, int patch) ParseVersionComponents(string version)
    {
        var parts = version.Split('.');
        var major = int.TryParse(parts[0], out var m) ? m : 0;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
        return (major * 1000 + minor, patch);
    }

    /// <summary>
    /// Download and extract cudart runtime libraries from the matched asset.
    /// Non-critical — failures are logged but don't block installation.
    /// </summary>
    private async Task<bool> DownloadCudaRuntimeAsync(
        CudaAsset? cudartAsset,
        string targetDirectory,
        CancellationToken ct)
    {
        if (cudartAsset == null)
        {
            LogMessage?.Invoke("[llama.cpp] No cudart asset to download");
            return true;
        }

        try
        {
            LogMessage?.Invoke($"[llama.cpp] Downloading CUDA {cudartAsset.CudaVersion} runtime libraries...");

            // Create temp file for the cudart archive
            var tempArchive = Path.Combine(_downloadsDir, $"cudart-{cudartAsset.CudaVersion}.tar.gz");

            // Download the cudart archive
            using var response = await _http.GetAsync(cudartAsset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                LogMessage?.Invoke($"[llama.cpp] cudart download failed: {(int)response.StatusCode} {response.StatusCode}");
                return false;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var fileStream = new FileStream(tempArchive, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await stream.CopyToAsync(fileStream, 81920, ct);

            // Extract to target directory
            var stagingDir = Path.Combine(_downloadsDir, "cudart-staging");
            Directory.CreateDirectory(stagingDir);

            try
            {
                await ExtractTarGzAsync(tempArchive, stagingDir, ct);

                // Copy extracted files to target directory
                CopyDirectoryContents(stagingDir, targetDirectory);
                LogMessage?.Invoke("[llama.cpp] CUDA runtime libraries installed");

                // Make binaries executable on Unix
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    MakeBinariesExecutable(targetDirectory);
                }
            }
            finally
            {
                // Clean up staging and temp archive
                DeleteDirectory(stagingDir);
                try { File.Delete(tempArchive); } catch { }
            }

            return true;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[llama.cpp] cudart download error (non-fatal): {ex.Message}");
            return false;
        }
    }

    // ---- End CUDA Version-Aware Asset Selection ----
}

// Minimal tar.gz reader (since System.IO.Compression doesn't include TarFile in .NET 9 on all platforms)
internal sealed class TarEntry
{
    public string Name { get; set; } = "";
    public bool IsDirectory { get; set; }
    public System.IO.Stream? DataStream { get; set; }
}

internal sealed class TarArchive : IDisposable
{
    private readonly System.IO.Stream _baseStream;
    private readonly System.IO.Compression.GZipStream _gzip;
    private System.IO.BinaryReader? _reader;

    public System.Collections.Generic.IList<TarEntry> Entries { get; private set; } = new List<TarEntry>();

    public TarArchive(System.IO.Stream baseStream)
    {
        _baseStream = baseStream;
        _gzip = new System.IO.Compression.GZipStream(baseStream, System.IO.Compression.CompressionMode.Decompress);
        _reader = new System.IO.BinaryReader(_gzip);
        ParseEntries();
    }

    private void ParseEntries()
    {
        var entries = new List<TarEntry>();
        var buffer = new byte[512];

        try
        {
            while (ReadBlock(buffer))
            {
                // Check for end of archive (all zeros)
                if (buffer.All(b => b == 0))
                    break;

                var name = System.Text.Encoding.ASCII.GetString(buffer, 0, 100).TrimEnd('\0');
                var sizeStr = System.Text.Encoding.ASCII.GetString(buffer, 124, 12).TrimEnd('\0');
                var isDirectory = (buffer[156] & 0xF0) == 0x80; // typeflag '5' = directory

                   long entrySize = 0;
                // Parse octal size manually (NumberStyles doesn't support Octal)
                if (!string.IsNullOrWhiteSpace(sizeStr))
                {
                    try
                    {
                        entrySize = Convert.ToInt64(sizeStr.Trim(), 8);
                    }
                    catch { }
                }

                // Pad to 512-byte boundary
                var dataPadding = entrySize % 512 == 0 ? 0 : 512 - (entrySize % 512);

                var entry = new TarEntry
                {
                    Name = name,
                    IsDirectory = isDirectory
                };

                if (!isDirectory && entrySize > 0)
                {
                    // Read data into a memory stream
                    var dataStream = new System.IO.MemoryStream();
                    var readBuffer = new byte[Math.Min((int)Math.Min(entrySize, 65536), 512)];
                    var remaining = entrySize;

                    while (remaining > 0)
                    {
                        var toRead = (int)Math.Min(remaining, readBuffer.Length);
                        var bytesRead = _reader!.BaseStream.Read(readBuffer, 0, toRead);
                        if (bytesRead == 0) break;
                        dataStream.Write(readBuffer, 0, bytesRead);
                        remaining -= bytesRead;
                    }

                    // Skip padding
                    if (dataPadding > 0)
                    {
                        var padBuffer = new byte[dataPadding];
                        _reader.BaseStream.Read(padBuffer, 0, padBuffer.Length);
                    }

                    dataStream.Position = 0;
                    entry.DataStream = dataStream;
                }

                entries.Add(entry);
            }
        }
        catch { }

        Entries = entries;
    }

    private bool ReadBlock(byte[] buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var bytesRead = _reader!.BaseStream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (bytesRead == 0) return totalRead > 0; // partial read at EOF is OK
            totalRead += bytesRead;
        }
        return true;
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _gzip?.Dispose();
        _baseStream?.Dispose();
    }
}
