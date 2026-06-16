using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <param name="preferredCudaVersion">Optional forced CUDA version (e.g. "12.4"). Null = auto-detect.</param>
    /// <returns>True if install succeeded, false otherwise.</returns>
    public async Task<bool> DownloadAndInstallAsync(
        string targetDirectory,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        string? preferredCudaVersion = null)
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
        var assetInfo = DetectAssetForPlatform(tag, release, preferredCudaVersion);
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
        LogMessage?.Invoke($"[llama.cpp] CheckForUpdateAsync called with targetDirectory: {targetDirectory}");
         var release = await GetLatestReleaseAsync(ct);
        var remoteTag = release?.GetProperty("tag_name").GetString();

        // Use GitHub tag as-is (it's already in the correct format, e.g. "b9660")
        string? remoteVersion = remoteTag?.Trim();

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
        string tag, JsonElement? release, string? preferredCudaVersion = null)
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
                var cudaVersion = preferredCudaVersion ?? CudaVersionDetector.GetCudaVersion();
                var llamaCudaAssets = allCudaAssets.Where(a => a.AssetType == CudaAssetType.LlamaBuild).ToList();

                if (llamaCudaAssets.Any())
                {
                    if (!string.IsNullOrEmpty(cudaVersion))
                    {
                        // Version-aware selection for CUDA builds
                        var bestAsset = FindBestCudaAsset(llamaCudaAssets, cudaVersion);
                        if (bestAsset != null)
                        {
                            var source = preferredCudaVersion != null ? $"forced ({preferredCudaVersion})" : "auto-detected";
                            LogMessage?.Invoke($"[llama.cpp] CUDA version-aware selection ({source}): {bestAsset.Name}");
                            return new DetectedAsset(
                                bestAsset.Name,
                                bestAsset.Size,
                                bestAsset.Url,
                                bestAsset.Digest,
                                cudartAssets);
                        }
                    }

                    // Fallback: no CUDA version detected — pick latest CUDA build
                    // User has CUDA selected but toolkit not installed; give them the newest CUDA
                    if (cudaVersion == null)
                    {
                        var latestAsset = llamaCudaAssets.OrderByDescending(a => a.Name).FirstOrDefault();
                        if (latestAsset != null)
                        {
                            LogMessage?.Invoke($"[llama.cpp] No CUDA version detected — selecting latest CUDA build: {latestAsset.Name}");
                            return new DetectedAsset(
                                latestAsset.Name,
                                latestAsset.Size,
                                latestAsset.Url,
                                latestAsset.Digest,
                                cudartAssets);
                        }
                    }
                }
            }
        }

        // Non-CUDA path: use existing pattern-based detection
        var arch = RuntimeInformation.ProcessArchitecture;
        var archFilter = arch == Architecture.Arm64 ? "arm64" : "x64";
        var archiveFormat = isWindows ? ".zip" : ".tar.gz";

        // Filter patterns: if no GPU was detected (only CpuOnly), only use CPU fallback patterns.
        // This prevents selecting CUDA/Vulkan assets when user has GPU preference but no hardware.
        var detectedBackends = GpuDetectionService.DetectBackends();
        var hasGpu = detectedBackends.Any(g => g.Backend != GpuDetectionService.GpuBackend.CpuOnly);
        var filteredPatterns = patterns.Where(p =>
        {
            if (p == null) return false;
            if (hasGpu) return true;
            // No GPU detected — only allow CPU fallback patterns
            return p.Contains("-cpu-");
        }).ToList();

        // Try each pattern in priority order
        foreach (var pattern in filteredPatterns)
        {
            if (pattern == null) continue;

            foreach (var asset in assetArray)
            {
                var name = asset.GetProperty("name").GetString() ?? "";

                // Match pattern + architecture + correct archive format
                if (name.Contains(pattern) &&
                    name.EndsWith(archiveFormat) &&
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
                if (name.Contains("-win-cpu-") && name.EndsWith(".zip") && name.Contains(archFilter))
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

        // Kill any running llama-server/llama-cpp processes before install
        LogMessage?.Invoke("[llama.cpp] Stopping llama-server processes before update...");
        await KillLlamaServerProcessesAsync(targetDirectory);
        await Task.Delay(1000); // Give processes time to release file handles

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
            var serverBin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "llama-server.exe"
                : "llama-server";
            if (!File.Exists(Path.Combine(targetDirectory, serverBin)))
            {
                LogMessage?.Invoke($"[llama.cpp] Installed binary '{serverBin}' not found — rollback");
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
                        FileName = "/usr/bin/xattr",
                        Arguments = $"-d com.apple.quarantine \"{targetDirectory}\"",
                        UseShellExecute = false,
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
        // Decompress gzip and parse tar manually — more reliable than custom TarArchive
        using var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        
        // Extract top-level directory from archive filename (e.g., llama-b9660-bin-macos-arm64.tar.gz → llama-b9660/)
          var archiveName = Path.GetFileName(archivePath); // llama-b9660-bin-macos-arm64.tar.gz
          // Remove .tar.gz or .tgz
          archiveName = System.Text.RegularExpressions.Regex.Replace(archiveName, @"\.tar\.gz$|\.tgz$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
          // llama-b9660-bin-macos-arm64
          // Remove known suffixes: -bin-macos-arm64, -bin-linux-x64, etc.
          archiveName = System.Text.RegularExpressions.Regex.Replace(archiveName, @"-bin-(?:macos|linux|windows)-?(?:arm64|x64|x86_64)?$", "");
          // Remove trailing -macos, -linux, -windows if present
          archiveName = System.Text.RegularExpressions.Regex.Replace(archiveName, @"-(?:macos|linux|windows)$", "");
          var topDir = archiveName + "/";
        
        // Read all decompressed tar data into memory
        var tarData = new byte[81920];
        var totalRead = 0;
        int bytesRead;
        while ((bytesRead = await gzip.ReadAsync(tarData, totalRead, tarData.Length - totalRead, ct)) > 0)
        {
            totalRead += bytesRead;
            if (totalRead == tarData.Length)
            {
                Array.Resize(ref tarData, tarData.Length * 2);
            }
        }
        if (totalRead < tarData.Length)
        {
            Array.Resize(ref tarData, totalRead);
        }
        
        LogMessage?.Invoke($"[llama.cpp] Top-level directory: '{topDir}' (from '{archivePath}')");
            
            // Parse tar entries (512-byte blocks)
        var offset = 0;
        var blockCount = tarData.Length / 512;
        var entriesFound = 0;
        
        for (var i = 0; i < blockCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            
            // Check for end-of-archive (two blocks of 512 zero bytes) — only at the very end
            if (i + 2 >= blockCount)
            {
                var allZero = true;
                for (var b = 0; b < 1024; b++)
                {
                    if (tarData[offset + b] != 0) { allZero = false; break; }
                }
                if (allZero) break;
            }
            
            // Parse header
            var name = System.Text.Encoding.ASCII.GetString(tarData, offset, 100).TrimEnd('\0');
            if (string.IsNullOrEmpty(name)) { offset += 512; continue; }
            
            // Skip invalid entries (Mach-O segments have garbage names with null chars)
            if (name.IndexOf('\0') >= 0 || name.Any(c => c < 32 && c != '\0'))
            {
                offset += 512;
                continue;
            }
            
            entriesFound++;
            
            // Type flag at offset 156 — 0 or empty = regular file, 5 = directory
            var typeFlag = (char)tarData[offset + 156];
            var isDirectory = typeFlag == '5';
            
            // Size at offset 124, 12 bytes octal
            var sizeStr = System.Text.Encoding.ASCII.GetString(tarData, offset + 124, 12).TrimEnd('\0', ' ');
            long fileSize = 0;
            try { fileSize = Convert.ToInt64(sizeStr, 8); } catch { }
            
            // Remove top-level directory prefix (from filename, not from parsing)
            var targetPath = name;
            if (name.StartsWith(topDir))
            {
                targetPath = name.Substring(topDir.Length).TrimStart('/');
            }
            // Skip the top-level directory entry itself (don't create subdirectory)
            if (string.IsNullOrEmpty(targetPath))
            {
                offset += 512;
                continue;
            }
            
            var fullPath = Path.Combine(destinationDir, targetPath);
            
            if (isDirectory)
            {
                LogMessage?.Invoke($"[llama.cpp] Tar entry (dir): {name} -> {fullPath}");
                Directory.CreateDirectory(fullPath);
            }
            else if (fileSize > 0)
            {
                var fullDir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(fullDir) && !Directory.Exists(fullDir))
                    Directory.CreateDirectory(fullDir);
                
                LogMessage?.Invoke($"[llama.cpp] Tar entry (file): {name} -> {fullPath} ({fileSize} bytes)");
                
                using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var dataOffset = offset + 512;
                var remaining = fileSize;
                var buffer = new byte[Math.Min((int)Math.Min(remaining, 65536), 81920)];
                var totalWritten = 0;
                
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(remaining, buffer.Length);
                    if (dataOffset + toRead > tarData.Length) break;
                    await fs.WriteAsync(tarData, dataOffset, toRead, ct);
                    dataOffset += toRead;
                    remaining -= toRead;
                    totalWritten += toRead;
                }
                
                if (name.Contains("llama-server"))
                {
                    LogMessage?.Invoke($"[llama.cpp] Extracted llama-server: {totalWritten} bytes (expected {fileSize})");
                }
                
                // Skip padding to 512-byte boundary
                var dataPadding = fileSize % 512;
                if (dataPadding > 0)
                {
                    dataOffset += (int)(512 - dataPadding);
                }
            }
            
            offset += 512;
        }
        
        LogMessage?.Invoke($"[llama.cpp] Extracted {entriesFound} entries to {destinationDir}");
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
            var sourceSize = new System.IO.FileInfo(file).Length;
            if (Path.GetFileName(file).Contains("llama-server"))
            {
                LogMessage?.Invoke($"[llama.cpp] CopyDirectoryContents: {Path.GetFileName(file)} -> {dest} ({sourceSize} bytes)");
            }
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
        // Remove old symlinks first (they may point to old versioned dylibs)
        foreach (var file in Directory.GetFiles(directory, "*.dylib"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var parts = name.Split('.');
            if (parts.Length >= 3 && parts[^1] == "dylib")
            {
                // Check if this looks like a symlink target (versioned) or a symlink
                // Remove any existing symlink files
                try
                {
                    if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint) ||
                        IsSymlink(file))
                    {
                        File.Delete(file);
                    }
                }
                catch { }
            }
        }

        // Set executable permission using .NET native API (more reliable than chmod subprocess)
        foreach (var file in Directory.GetFiles(directory))
        {
            var name = Path.GetFileName(file);
            // Make known binary names executable
            if (name.StartsWith("llama") || name.StartsWith("ggml") || name == "rpc-server")
            {
                try
                {
                    var attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    {
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                    }
                    // On Unix, set execute permission
                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "/bin/sh",
                            Arguments = $"-c \"chmod +x '{file}'\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardError = true
                        };
                        using var proc = System.Diagnostics.Process.Start(psi);
                        proc?.WaitForExit(3000);
                        var err = proc?.StandardError.ReadToEnd()?.Trim();
                        if (!string.IsNullOrEmpty(err))
                        {
                            LogMessage?.Invoke($"[llama.cpp] chmod warning for {name}: {err}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"[llama.cpp] chmod error for {name}: {ex.Message}");
                }
            }
        }

         // Create versioned .dylib symlinks for NEWEST version only
        // Pattern: libfoo.0.0.9660.dylib → libfoo.0.0.dylib → libfoo.0.dylib
        var dylibGroups = new Dictionary<string, (string path, int minor, int patch)>();
        foreach (var file in Directory.GetFiles(directory, "*.dylib"))
        {
            var name = Path.GetFileNameWithoutExtension(file); // e.g. libllama-common.0.0.9660
            var parts = name.Split('.');
            if (parts.Length >= 4 &&
                int.TryParse(parts[^3], out var major) &&
                int.TryParse(parts[^2], out var minor) &&
                int.TryParse(parts[^1], out var patch) &&
                !(parts[^2] == "0" && parts[^1] == "0"))
            {
                var baseName = string.Join(".", parts.Take(parts.Length - 2));
               if (!dylibGroups.ContainsKey(baseName) ||
                    dylibGroups[baseName].minor < minor ||
                    (dylibGroups[baseName].minor == minor && dylibGroups[baseName].patch < patch))
                {
                    dylibGroups[baseName] = (file, minor, patch);
                }
            }
        }

        foreach (var group in dylibGroups.Values)
        {
            var name = Path.GetFileNameWithoutExtension(group.path);
            var parts = name.Split('.');
            var link1 = string.Join(".", parts.Take(parts.Length - 1)) + ".dylib";
            var link1Path = Path.Combine(directory, link1);
            try { CreateSymlink(link1Path, name + ".dylib"); } catch { }

            var link2 = string.Join(".", parts.Take(parts.Length - 2)) + ".dylib";
            var link2Path = Path.Combine(directory, link2);
            try { CreateSymlink(link2Path, link1); } catch { }
        }
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            var info = new System.IO.FileInfo(path);
            return info.LinkTarget != null;
        }
        catch { return false; }
    }

    private static void CreateSymlink(string linkPath, string target)
    {
        // Remove existing file/symlink at linkPath
        if (File.Exists(linkPath)) File.Delete(linkPath);
        // Create symlink using ln -s
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/ln",
            Arguments = $"-sf \"{target}\" \"{linkPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        proc?.WaitForExit(3000);
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

        /// <summary>
        /// Kill any running llama-server/llama-cpp processes to release file handles before install.
        /// Uses graceful SIGTERM first, then forceful kill as fallback.
        /// </summary>
        private async Task KillLlamaServerProcessesAsync(string targetDirectory)
        {
            var serverExe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "llama-server.exe"
                : "llama-server";

            // Find processes that have the target directory's binary in use
            var serverPath = Path.Combine(targetDirectory, serverExe);
            var processesToKill = new List<Process>();

            try
            {
                foreach (var p in Process.GetProcessesByName("llama-server"))
                {
                    try
                    {
                        if (p.MainModule?.FileName == serverPath ||
                            p.MainModule?.FileName?.Contains("llama-server") == true)
                        {
                            processesToKill.Add(p);
                        }
                    }
                    catch { /* Process may have exited */ }
                }
            }
            catch { }

            if (processesToKill.Count == 0)
            {
                LogMessage?.Invoke("[llama.cpp] No running llama-server processes found");
                return;
            }

            LogMessage?.Invoke($"[llama.cpp] Found {processesToKill.Count} llama-server process(es) to stop");

            // Phase 1: Graceful shutdown via SIGTERM/taskkill
            foreach (var p in processesToKill)
            {
                try
                {
                    LogMessage?.Invoke($"[llama.cpp] Sending graceful stop to llama-server pid={p.Id}");
                    if (!p.HasExited)
                    {
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            var psi = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "taskkill.exe",
                                Arguments = $"/T /PID {p.Id}",
                                UseShellExecute = true,
                                CreateNoWindow = true,
                                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                            };
                            using var tk = Process.Start(psi);
                            tk?.WaitForExit();
                        }
                        else
                        {
                            p.Kill(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"[llama.cpp] Graceful stop failed pid={p.Id}: {ex.Message}");
                }
            }

            // Wait for processes to exit
            var deadline = DateTime.Now.AddSeconds(5);
            foreach (var p in processesToKill)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.WaitForExit((int)(deadline - DateTime.Now).TotalMilliseconds);
                    }
                }
                catch { }
            }

            // Phase 2: Force kill any remaining processes
            foreach (var p in processesToKill)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        LogMessage?.Invoke($"[llama.cpp] Force killing llama-server pid={p.Id}");
                        p.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"[llama.cpp] Force kill failed pid={p.Id}: {ex.Message}");
                }
            }

            // Final wait
            foreach (var p in processesToKill)
            {
                try
                {
                    if (!p.HasExited)
                        p.WaitForExit(2000);
                }
                catch { }
            }

            LogMessage?.Invoke("[llama.cpp] llama-server processes stopped");
        }

        // ---- Local Version Detection ----

        private string? DetectLocalVersion(string targetDirectory)
    {
        // Try to find llama-server binary and check version
        var serverPath = Path.Combine(targetDirectory, "llama-server");
        LogMessage?.Invoke($"[llama.cpp] DetectLocalVersion checking: {serverPath} (exists: {File.Exists(serverPath)})");
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

        // Clean up old symlinks that may point to old versioned dylibs
        // (these cause the binary to crash when loading incompatible dylibs)
        try
        {
            foreach (var file in Directory.GetFiles(targetDirectory, "*.dylib"))
            {
                if (IsSymlink(file))
                {
                    File.Delete(file);
                    LogMessage?.Invoke($"[llama.cpp] DetectLocalVersion removed old symlink: {Path.GetFileName(file)}");
                }
            }

             // Create symlinks for NEWEST versioned dylibs only (old versions overwrite if we create all)
            // Group by base name, find highest version, create symlinks only for that
            var dylibGroups = new Dictionary<string, (string path, int minor, int patch)>();
            foreach (var file in Directory.GetFiles(targetDirectory, "*.dylib"))
            {
                var name = Path.GetFileNameWithoutExtension(file); // e.g. libllama-common.0.0.9660
                var parts = name.Split('.');
                if (parts.Length >= 4 &&
                    int.TryParse(parts[^3], out var major) &&
                    int.TryParse(parts[^2], out var minor) &&
                    int.TryParse(parts[^1], out var patch) &&
                    !(parts[^2] == "0" && parts[^1] == "0"))
                {
                    var baseName = string.Join(".", parts.Take(parts.Length - 2)); // e.g. libllama-common.0
                    if (!dylibGroups.ContainsKey(baseName) ||
                    dylibGroups[baseName].minor < minor ||
                    (dylibGroups[baseName].minor == minor && dylibGroups[baseName].patch < patch))
                    {
                        dylibGroups[baseName] = (file, minor, patch);
                    }
                }
            }

            // Create symlinks only for the highest version of each library
            foreach (var group in dylibGroups.Values)
            {
                var name = Path.GetFileNameWithoutExtension(group.path);
                var parts = name.Split('.');
                var link1 = string.Join(".", parts.Take(parts.Length - 1)) + ".dylib";
                var link1Path = Path.Combine(targetDirectory, link1);
                try { CreateSymlink(link1Path, name + ".dylib"); } catch { }

                var link2 = string.Join(".", parts.Take(parts.Length - 2)) + ".dylib";
                var link2Path = Path.Combine(targetDirectory, link2);
                try { CreateSymlink(link2Path, link1); } catch { }
            }
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[llama.cpp] DetectLocalVersion symlink cleanup warning: {ex.Message}");
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

            LogMessage?.Invoke($"[llama.cpp] DetectLocalVersion starting process: {serverPath} --version");
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
            {
                LogMessage?.Invoke("[llama.cpp] DetectLocalVersion Process.Start returned null");
                return null;
            }
            LogMessage?.Invoke($"[llama.cpp] DetectLocalVersion process handle obtained: {proc.Handle}");
            // Read streams FIRST (before WaitForExit can cause issues)
            var stderr = proc.StandardError.ReadToEnd();
            var stdout = proc.StandardOutput.ReadToEnd();
            var exited = proc.WaitForExit(5000);
            var exitCode = proc.ExitCode;
            var output = stderr + stdout;
            LogMessage?.Invoke($"[llama.cpp] DetectLocalVersion exited: {exited}, code: {exitCode}");
            LogMessage?.Invoke($"[llama.cpp] DetectLocalVersion output: '{output}'");

            // Parse version from output: "version: 9553 (9e3b928fd)"
                   // GitHub tags use the DECIMAL build number (e.g., b9660, b9659)
                   // Priority 1: Extract decimal build number, format as "b{decimal}"
                   var match = Regex.Match(output, @"version:\s+(\d+)", RegexOptions.IgnoreCase);
                   if (match.Success)
                   {
                       return $"b{match.Groups[1].Value}";
                   }

            // Priority 2: Fallback to commit hash (b + 5+ hex chars in parentheses)
            match = System.Text.RegularExpressions.Regex.Match(output, @"\(([0-9a-fA-F]{5,})\)");
            if (match.Success)
            {
                var hash = match.Groups[1].Value;
                // GitHub uses 'b' prefix + short hash (first 5 chars)
                return $"b{hash.Substring(0, Math.Min(5, hash.Length))}";
            }

            return null;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[llama.cpp] DetectLocalVersion EXCEPTION: {ex.GetType().Name}: {ex.Message}");
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
     private readonly Action<string>? _log;

     public System.Collections.Generic.IList<TarEntry> Entries { get; private set; } = new List<TarEntry>();

     public TarArchive(System.IO.Stream baseStream, Action<string>? log = null)
     {
         _baseStream = baseStream;
         _gzip = new System.IO.Compression.GZipStream(baseStream, System.IO.Compression.CompressionMode.Decompress);
         _reader = new System.IO.BinaryReader(_gzip);
         _log = log;
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
        catch (Exception ex)
              {
                  _log?.Invoke($"[TarArchive] Parse error: {ex.Message}");
              }

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
