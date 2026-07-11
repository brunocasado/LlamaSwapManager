using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

/// <summary>
/// Coordinates release discovery, artifact download, installation, CUDA runtime setup,
/// and local version detection for llama.cpp.
/// </summary>
public sealed class LlamaCppDownloader : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _downloadsDirectory;
    private readonly LlamaCppVersionDetector _versionDetector;
    private readonly GitHubReleaseClient _releaseClient;
    private readonly LlamaCppAssetSelector _assetSelector;
    private readonly LlamaCppArtifactDownloader _artifactDownloader;
    private readonly LlamaCppInstaller _installer;
    private readonly CudaRuntimeInstaller _cudaRuntimeInstaller;

    public event Action<string>? LogMessage;

    public LlamaCppDownloader(string? userDirectory = null)
    {
        var rootDirectory = userDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".llama-swap");
        _downloadsDirectory = Path.Combine(rootDirectory, ".updates");

        _http = CreateHttpClient();
        var processManager = new LlamaCppProcessManager(Log);
        var platformConfigurator = new LlamaCppPlatformConfigurator(Log);
        _versionDetector = new LlamaCppVersionDetector(platformConfigurator, Log);
        _releaseClient = new GitHubReleaseClient(
            _http,
            "ggml-org/llama.cpp",
            Path.Combine(_downloadsDirectory, "github-cache"),
            Log);
        _assetSelector = new LlamaCppAssetSelector(Log);
        _artifactDownloader = new LlamaCppArtifactDownloader(_http, Log);
        _installer = new LlamaCppInstaller(
            _downloadsDirectory,
            processManager,
            platformConfigurator,
            Log);
        _cudaRuntimeInstaller = new CudaRuntimeInstaller(
            _downloadsDirectory,
            _artifactDownloader,
            _installer,
            platformConfigurator,
            Log);
    }

    public async Task<bool> DownloadAndInstallAsync(
        string targetDirectory,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        string? preferredCudaVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        progress?.Report(0.0);
        Log("[llama.cpp] Starting download of latest release...");

        progress?.Report(0.05);
        var release = await _releaseClient.GetLatestReleaseAsync(ct);
        if (release is null)
        {
            Log("[llama.cpp] Failed to fetch latest release info");
            return false;
        }

        var tag = release.Value.GetProperty("tag_name").GetString() ?? string.Empty;
        Log($"[llama.cpp] Latest version: {tag}");

        progress?.Report(0.15);
        var asset = _assetSelector.DetectAssetForPlatform(tag, release, preferredCudaVersion);
        if (asset is null)
        {
            Log("[llama.cpp] No suitable asset found for this platform");
            return false;
        }

        Log($"[llama.cpp] Selected asset: {asset.Name} ({FormatSize(asset.Size)})");

        var tempDirectory = CreateTempDirectory(tag);
        try
        {
            var archivePath = Path.Combine(tempDirectory, asset.Name);

            progress?.Report(0.20);
            if (!await _artifactDownloader.DownloadAsync(asset.Url, archivePath, progress, ct))
            {
                Log("[llama.cpp] Download failed or cancelled");
                return false;
            }

            progress?.Report(0.70);
            if (!await _artifactDownloader.VerifyChecksumAsync(archivePath, asset.Digest, ct))
            {
                Log("[llama.cpp] Checksum verification failed — aborting");
                return false;
            }

            Log("[llama.cpp] Checksum verified OK");

            progress?.Report(0.80);
            var installed = await _installer.InstallAsync(
                tempDirectory,
                archivePath,
                targetDirectory,
                ct);

            if (installed &&
                GpuDetectionSettings.GetEffectiveBackend() == GpuDetectionService.GpuBackend.Cuda &&
                asset.CudartAssets.Count > 0)
            {
                progress?.Report(0.90);
                await _cudaRuntimeInstaller.InstallAsync(
                    asset.CudartAssets.FirstOrDefault(),
                    targetDirectory,
                    ct);
            }

            Log(installed
                ? $"[llama.cpp] Install complete — version {tag} in {targetDirectory}"
                : "[llama.cpp] Install failed — check logs for details");

            progress?.Report(1.0);
            return installed;
        }
        finally
        {
            LlamaCppInstaller.DeleteDirectory(tempDirectory);
        }
    }

    public async Task<(bool HasUpdate, string? RemoteVersion, string? LocalVersion)> CheckForUpdateAsync(
        string targetDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        Log($"[llama.cpp] CheckForUpdateAsync called with targetDirectory: {targetDirectory}");

        var release = await _releaseClient.GetLatestReleaseAsync(ct);
        var remoteVersion = release?.GetProperty("tag_name").GetString()?.Trim();
        var localVersion = await _versionDetector.DetectAsync(targetDirectory, ct);

        return (
            HasUpdate: remoteVersion is not null &&
                       !string.Equals(remoteVersion, localVersion, StringComparison.Ordinal),
            RemoteVersion: remoteVersion,
            LocalVersion: localVersion);
    }

    public void Dispose() => _http.Dispose();

    private void Log(string message) => LogMessage?.Invoke(message);

    private string CreateTempDirectory(string tag)
    {
        Directory.CreateDirectory(_downloadsDirectory);
        var safeTag = string.IsNullOrWhiteSpace(tag) ? "unknown" : tag;
        var directory = Path.Combine(
            _downloadsDirectory,
            $"llama-cpp-{safeTag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LlamaSwapManager");

        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
