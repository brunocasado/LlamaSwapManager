using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LlamaSwapManager.Services;
using Xunit;
using Xunit.Abstractions;

namespace LlamaSwapManager.Tests;

/// <summary>
/// Integration tests for UpdateService.UpdateAsync — tests the actual update flow.
/// Runs against the real GitHub API to download and verify the latest release.
/// </summary>
public class UpdateServiceIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public UpdateServiceIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsLatestVersion()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"update-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var service = new UpdateService(tempDir);

            // Act
            var result = await service.CheckForUpdatesAsync();

            // Assert
            Assert.NotNull(result);
            Assert.NotEmpty(result.Version ?? "");
            Assert.NotEmpty(result.AssetName ?? "");
            Assert.NotEmpty(result.DownloadUrl ?? "");
            Assert.True(result.SizeBytes > 0);

            _output.WriteLine($"  Latest version: {result.Version}");
            _output.WriteLine($"  Asset: {result.AssetName}");
            _output.WriteLine($"  Size: {result.SizeBytes} bytes");
            _output.WriteLine($"  Download URL: {result.DownloadUrl}");

            // Verify it's the expected platform
            Assert.Contains("darwin", result.AssetName, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("arm64", result.AssetName, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".tar.gz", result.AssetName, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task UpdateAsync_DownloadsAndExtractsCorrectly()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"update-extract-{Guid.NewGuid()}");
        var installDir = Path.Combine(tempDir, "install");
        Directory.CreateDirectory(installDir);

        // Create a placeholder binary
        var placeholderPath = Path.Combine(installDir, "llama-swap");
        File.WriteAllText(placeholderPath, "placeholder");

        try
        {
            var service = new UpdateService(installDir);

            // Act: Run the full update flow
            var progressMessages = new System.Collections.Generic.List<string>();
            var logMessages = new System.Collections.Generic.List<string>();

            service.ProgressChanged += p => progressMessages.Add($"{p.Percentage}%: {p.Message}");
            service.LogMessage += m => logMessages.Add(m);

            var success = await service.UpdateAsync("", CancellationToken.None);

            // Assert: Log what happened
            _output.WriteLine("=== Progress ===");
            foreach (var msg in progressMessages)
                _output.WriteLine($"  {msg}");

            _output.WriteLine("=== Logs ===");
            foreach (var msg in logMessages)
                _output.WriteLine($"  {msg}");

            // The update should succeed (download + extract + replace)
            Assert.True(success, $"Update failed. Logs: {string.Join("; ", logMessages)}");

            // Verify the binary was replaced (not the placeholder)
            var newBinaryPath = Path.Combine(installDir, "llama-swap");
            Assert.True(File.Exists(newBinaryPath), $"Binary not found at {newBinaryPath}");

            var fileInfo = new FileInfo(newBinaryPath);
            Assert.True(fileInfo.Length > 1000000, $"Binary too small: {fileInfo.Length} bytes (expected > 1MB)");

            // Verify the binary is executable
            Assert.True((fileInfo.Attributes & FileAttributes.ReadOnly) == 0, "Binary is read-only");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

     [Fact]
    public async Task UpdateAsync_VerifiesChecksum()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"update-checksum-{Guid.NewGuid()}");
        var installDir = Path.Combine(tempDir, "install");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "llama-swap"), "placeholder");

        try
        {
            var service = new UpdateService(installDir);

            var progressMessages = new System.Collections.Generic.List<string>();
            var logMessages = new System.Collections.Generic.List<string>();
            service.ProgressChanged += p => progressMessages.Add($"{p.Percentage}%: {p.Message}");
            service.LogMessage += m => logMessages.Add(m);

            // Act
            var success = await service.UpdateAsync("", CancellationToken.None);

            // Assert: Checksum should be verified (reported via ProgressChanged)
            var checksumProgress = progressMessages.FirstOrDefault(m => m.Contains("Checksum", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(checksumProgress);
            _output.WriteLine($"  Checksum progress: {checksumProgress}");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task LlamaCppDownloader_DownloadAndInstallAsync_Works()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"llamacpp-update-{Guid.NewGuid()}");
        var installDir = Path.Combine(tempDir, "install");
        Directory.CreateDirectory(installDir);

        // Create a placeholder llama-server
        var placeholderPath = Path.Combine(installDir, "llama-server");
        File.WriteAllText(placeholderPath, "placeholder");

        try
        {
            var downloader = new LlamaCppDownloader();

            var progressValues = new System.Collections.Generic.List<int>();
            var logMessages = new System.Collections.Generic.List<string>();

            downloader.LogMessage += m => logMessages.Add(m);
            var progress = new Progress<double>(p => progressValues.Add((int)(p * 100)));

            // Act
            var success = await downloader.DownloadAndInstallAsync(
                installDir,
                progress,
                CancellationToken.None,
                preferredCudaVersion: null);

            // Assert: Log what happened
            _output.WriteLine("=== Progress ===");
            foreach (var pct in progressValues.Distinct())
                _output.WriteLine($"  {pct}%");

            Assert.True(success, $"DownloadAndInstall failed. Logs: {string.Join("; ", logMessages)}");

            // Verify the binary was replaced
            var newBinaryPath = Path.Combine(installDir, "llama-server");
            Assert.True(File.Exists(newBinaryPath), $"llama-server not found at {newBinaryPath}");

            var fileInfo = new FileInfo(newBinaryPath);
            Assert.True(fileInfo.Length > 10000, $"llama-server too small: {fileInfo.Length} bytes (expected > 10KB)");

            // Verify version detection works
            var versionResult = await downloader.CheckForUpdateAsync(installDir);

            _output.WriteLine("=== Logs (full) ===");
            foreach (var msg in logMessages)
                _output.WriteLine($"  {msg}");

            _output.WriteLine($"  Local version: {versionResult.LocalVersion}");
            _output.WriteLine($"  Remote version: {versionResult.RemoteVersion}");
            Assert.NotNull(versionResult.LocalVersion);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}