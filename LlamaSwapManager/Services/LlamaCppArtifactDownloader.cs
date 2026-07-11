using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

internal sealed class LlamaCppArtifactDownloader
{
    private const long MinimumFreeSpaceBytes = 500L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly Action<string>? _log;

    public LlamaCppArtifactDownloader(HttpClient http, Action<string>? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log;
    }

    public async Task<bool> DownloadAsync(
        string url,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        try
        {
            var parentDirectory = Path.GetDirectoryName(destinationPath);
            if (parentDirectory is not null &&
                !HasEnoughDiskSpace(parentDirectory, MinimumFreeSpaceBytes))
            {
                _log?.Invoke("[llama.cpp] Insufficient disk space (need 500MB free)");
                return false;
            }

            using var response = await _http.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            if (!response.IsSuccessStatusCode)
            {
                _log?.Invoke(
                    $"[llama.cpp] Download error: {(int)response.StatusCode} {response.StatusCode}");
                return false;
            }

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var destination = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);

            var buffer = new byte[81920];
            var totalRead = 0L;
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer, ct)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;

                if (totalBytes > 0 && progress is not null)
                {
                    var downloadProgress = 0.20 + (0.50 * totalRead / totalBytes);
                    progress.Report(Math.Min(downloadProgress, 0.70));
                }
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            _log?.Invoke("[llama.cpp] Download timed out");
            return false;
        }
        catch (HttpRequestException ex)
        {
            _log?.Invoke($"[llama.cpp] Download network error: {ex.Message}");
            return false;
        }
        catch (IOException ex)
        {
            _log?.Invoke($"[llama.cpp] Download file error: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> VerifyChecksumAsync(
        string filePath,
        string? expectedDigest,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (string.IsNullOrWhiteSpace(expectedDigest) ||
            !expectedDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            _log?.Invoke("[llama.cpp] No checksum available — skipping verification (WARNING)");
            return true;
        }

        var expectedHash = expectedDigest["sha256:".Length..];

        try
        {
            await using var stream = File.OpenRead(filePath);
            var hashBytes = await SHA256.HashDataAsync(stream, ct);
            var actualHash = Convert.ToHexString(hashBytes);

            if (actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                return true;

            _log?.Invoke(
                $"[llama.cpp] Checksum mismatch: expected {expectedHash}, got {actualHash.ToLowerInvariant()}");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException ex)
        {
            _log?.Invoke($"[llama.cpp] Checksum verification error: {ex.Message}");
            return false;
        }
    }

    internal static bool HasEnoughDiskSpace(string path, long requiredBytes)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            var drive = DriveInfo.GetDrives()
                .Where(candidate => candidate.IsReady)
                .Where(candidate => fullPath.StartsWith(
                    candidate.RootDirectory.FullName,
                    comparison))
                .OrderByDescending(candidate => candidate.RootDirectory.FullName.Length)
                .FirstOrDefault();

            return drive is null || drive.AvailableFreeSpace > requiredBytes;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
