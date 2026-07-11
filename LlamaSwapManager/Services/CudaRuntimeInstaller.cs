using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

internal sealed class CudaRuntimeInstaller
{
    private readonly string _downloadsDirectory;
    private readonly LlamaCppArtifactDownloader _artifactDownloader;
    private readonly LlamaCppInstaller _installer;
    private readonly LlamaCppPlatformConfigurator _platformConfigurator;
    private readonly Action<string>? _log;

    public CudaRuntimeInstaller(
        string downloadsDirectory,
        LlamaCppArtifactDownloader artifactDownloader,
        LlamaCppInstaller installer,
        LlamaCppPlatformConfigurator platformConfigurator,
        Action<string>? log = null)
    {
        _downloadsDirectory = downloadsDirectory;
        _artifactDownloader = artifactDownloader;
        _installer = installer;
        _platformConfigurator = platformConfigurator;
        _log = log;
    }

    public async Task<bool> InstallAsync(
        LlamaCppAssetSelector.CudaAsset? asset,
        string targetDirectory,
        CancellationToken ct)
    {
        if (asset is null)
        {
            _log?.Invoke("[llama.cpp] No cudart asset to download");
            return true;
        }

        Directory.CreateDirectory(_downloadsDirectory);
        var operationId = Guid.NewGuid().ToString("N");
        var archivePath = Path.Combine(
            _downloadsDirectory,
            $"cudart-{asset.CudaVersion}-{operationId}.tar.gz");
        var stagingDirectory = Path.Combine(
            _downloadsDirectory,
            $"cudart-staging-{operationId}");

        try
        {
            _log?.Invoke(
                $"[llama.cpp] Downloading CUDA {asset.CudaVersion} runtime libraries...");

            if (!await _artifactDownloader.DownloadAsync(
                    asset.Url,
                    archivePath,
                    progress: null,
                    ct))
            {
                return false;
            }

            if (!await _artifactDownloader.VerifyChecksumAsync(
                    archivePath,
                    asset.Digest,
                    ct))
            {
                _log?.Invoke("[llama.cpp] CUDA runtime checksum verification failed");
                return false;
            }

            Directory.CreateDirectory(stagingDirectory);
            ArchiveExtractor.ExtractTarGz(archivePath, stagingDirectory, ct);
            _installer.CopyDirectoryContents(stagingDirectory, targetDirectory, ct);
            _platformConfigurator.ConfigureBinaries(targetDirectory, ct);
            _log?.Invoke("[llama.cpp] CUDA runtime libraries installed");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log?.Invoke($"[llama.cpp] cudart installation error (non-fatal): {ex.Message}");
            return false;
        }
        finally
        {
            LlamaCppInstaller.DeleteDirectory(stagingDirectory);
            try { File.Delete(archivePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
