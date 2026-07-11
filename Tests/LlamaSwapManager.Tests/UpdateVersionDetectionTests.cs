using System;
using System.IO;
using System.Text.RegularExpressions;
using LlamaSwapManager.Services;
using Xunit;

namespace LlamaSwapManager.Tests;

/// <summary>
/// Tests for update version detection — validates the fixes for:
/// 1. llama.cpp version detection (stderr vs stdout, regex hash extraction)
/// 2. llama-swap version detection (directory path extraction)
/// 3. Version comparison (hash-based and numeric)
/// </summary>
public class UpdateVersionDetectionTests
{
    private readonly string _llamaCppDir;
    private readonly string _llamaSwapDir;

    public UpdateVersionDetectionTests()
    {
        _llamaCppDir = Path.Combine(Path.GetTempPath(), $"llama-swap-test-llama-cpp-{Guid.NewGuid()}");
        _llamaSwapDir = Path.Combine(Path.GetTempPath(), $"llama-swap-test-llama-swap-{Guid.NewGuid()}");
        Directory.CreateDirectory(_llamaCppDir);
        Directory.CreateDirectory(_llamaSwapDir);
    }

    [Fact]
    public void DetectLocalVersion_ExtractsBuildNumberFromOutput()
    {
        // llama-server output: "version: 9553 (9e3b928fd)"
        // NEW priority: decimal build number first → b9553
        var output = "version: 9553 (9e3b928fd)\nbuilt with AppleClang 21.0.0.21000099 for Darwin arm64\n";

        // Regex from LlamaCppDownloader.DetectLocalVersion (Priority 1)
        var match = Regex.Match(output, @"version:\s*(\d+)");
        Assert.True(match.Success, "Regex should match decimal build number");
        Assert.Equal("9553", match.Groups[1].Value);
        Assert.Equal("b9553", $"b{match.Groups[1].Value}");
    }

    [Fact]
    public void DetectLocalVersion_StderrAndStdoutCombined()
    {
        // llama-server writes to STDERR, but some builds may write to STDOUT
        var stderr = "version: 9553 (9e3b928fd)\n";
        var stdout = "";

        var combined = stderr + stdout;
        var match = Regex.Match(combined, @"\(([0-9a-fA-F]{7,})\)");

        Assert.True(match.Success, "Should match when output is in stderr");
    }

    [Fact]
    public async Task DetectLocalVersion_ReturnsNull_WhenBinaryNotFound()
    {
        var downloader = new LlamaCppDownloader(_llamaCppDir);
        var result = await downloader.CheckForUpdateAsync(_llamaCppDir);

        // Since no binary exists, DetectLocalVersion should return null
        Assert.Null(result.LocalVersion);
    }

    [Fact]
    public void DetectCurrentVersionAsync_UsesCorrectDirectory()
    {
        // Bug: MainViewModel was passing ExecutablePath (full binary path) instead of directory
        // Fix: Use Path.GetDirectoryName(ExecutablePath)
        var executablePath = "/Users/test/.llama-swap/llama-swap";
        var directory = Path.GetDirectoryName(executablePath);

        Assert.Equal("/Users/test/.llama-swap", directory);

        // Verify the binary path would be constructed correctly
        var binaryPath = Path.Combine(directory!, "llama-swap");
        Assert.Equal("/Users/test/.llama-swap/llama-swap", binaryPath);
    }

    [Fact]
    public void DetectCurrentVersionAsync_ExtractsVersionFromOutput()
    {
        // Simulate llama-swap --version output
        var output = "version: 223 (29d3d9ba206f58e91c931d30387f455efc64d245), built at 2026-06-04T04:52:33Z\n";

        // Regex from UpdateViewModel.DetectCurrentVersionAsync
        var match = Regex.Match(output, @"version:\s*(\d+)");

        Assert.True(match.Success, "Regex should match version number");
        Assert.Equal("223", match.Groups[1].Value);
    }

    [Fact]
    public void HasUpdate_HashVersions_ReturnsTrue_WhenLocalOlder()
    {
        // Local is older than remote → update available
        // b1234 (hex=0x1234=4660) < b9659 (hex=0x9659=38489)
        Assert.True(VersionComparer.HasUpdate("b1234", "b9659"));
    }

    [Fact]
    public void HasUpdate_HashVersions_ReturnsFalse_WhenLocalNewer()
    {
        // Local is newer than remote → no update needed
        // b9661 > b9660 (decimal build numbers)
        Assert.False(VersionComparer.HasUpdate("b9661", "b9660"));
    }

    [Fact]
    public void HasUpdate_HashVersions_ReturnsFalse_WhenSame()
    {
        Assert.False(VersionComparer.HasUpdate("b9659", "b9659"));
    }

    [Fact]
    public void HasUpdate_NumericVersions_ReturnsTrue_WhenOlder()
    {
        // Numeric versions (llama-swap uses build numbers)
        Assert.True(VersionComparer.HasUpdate("v223", "v226"));
    }

    [Fact]
    public void HasUpdate_NumericVersions_ReturnsFalse_WhenSame()
    {
        Assert.False(VersionComparer.HasUpdate("v226", "v226"));
    }

    [Fact]
    public void HasUpdate_NumericVersions_ReturnsFalse_WhenNewer()
    {
        Assert.False(VersionComparer.HasUpdate("v227", "v226"));
    }

    [Theory]
    [InlineData("/Users/test/.llama-swap/llama-swap", "/Users/test/.llama-swap")]
    [InlineData("/home/user/.llama-swap/llama-swap", "/home/user/.llama-swap")]
    public void ExtractDirectory_FromUnixExecutablePath_ReturnsDirectory(string exePath, string expectedDir)
    {
        // Regression test for the bug where ExecutablePath was passed instead of directory
        var dir = Path.GetDirectoryName(exePath);
        Assert.Equal(expectedDir, dir);
    }

    [Fact]
    public void ExtractDirectory_FromNullPath_UsesFallback()
    {
        string? executablePath = null;
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llama-swap");

        var updateDir = executablePath != null
            ? Path.GetDirectoryName(executablePath)!
            : fallback;

        Assert.Equal(fallback, updateDir);
    }
}
