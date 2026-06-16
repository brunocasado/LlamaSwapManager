using System;
using LlamaSwapManager.Services;
using Xunit;

namespace LlamaSwapManager.Tests;

/// <summary>
/// Tests for version comparison logic in VersionChecker.
/// </summary>
public class VersionComparisonTests
{
    private readonly VersionChecker _checker = new();

    #region llama-swap comparisons

    [Fact]
    public void Compare_LlamaSwap_UpdateAvailable_ReturnsCorrectResult()
    {
        var remote = new RemoteVersion
        {
            BinaryType = BinaryType.LlamaSwap,
            TagName = "v225",
            NumericValue = 225UL,
            DownloadUrl = "https://github.com/mostlygeek/llama-swap/releases/download/v225/..."
        };

        var local = new LocalVersion
        {
            BinaryType = BinaryType.LlamaSwap,
            VersionString = "v224",
            NumericValue = 224UL
        };

        var result = _checker.Compare(remote, local);

        Assert.True(result.HasUpdate);
        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal("v224", result.CurrentVersion);
        Assert.Equal("v225", result.LatestVersion);
    }

    [Fact]
    public void Compare_LlamaSwap_UpToDate_ReturnsCorrectResult()
    {
        var remote = new RemoteVersion
        {
            BinaryType = BinaryType.LlamaSwap,
            TagName = "v224",
            NumericValue = 224UL
        };

        var local = new LocalVersion
        {
            BinaryType = BinaryType.LlamaSwap,
            VersionString = "v224",
            NumericValue = 224UL
        };

        var result = _checker.Compare(remote, local);

        Assert.False(result.HasUpdate);
        Assert.Equal(UpdateStatus.UpToDate, result.Status);
    }

    [Fact]
    public void Compare_LlamaSwap_LocalNewer_ReturnsUpToDate()
    {
        // Edge case: local version is higher than remote (shouldn't happen in practice)
        var remote = new RemoteVersion
        {
            BinaryType = BinaryType.LlamaSwap,
            TagName = "v223",
            NumericValue = 223UL
        };

        var local = new LocalVersion
        {
            BinaryType = BinaryType.LlamaSwap,
            VersionString = "v224",
            NumericValue = 224UL
        };

        var result = _checker.Compare(remote, local);

        Assert.False(result.HasUpdate);
        Assert.Equal(UpdateStatus.UpToDate, result.Status);
    }

    #endregion

    #region llama.cpp comparisons

    [Fact]
    public void Compare_LlamaCpp_UpdateAvailable_ReturnsCorrectResult()
    {
        var remote = new RemoteVersion
        {
            BinaryType = BinaryType.LlamaCpp,
            TagName = "b9612",
            NumericValue = 0x9612UL,
            DownloadUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b9612/..."
        };

        var local = new LocalVersion
        {
            BinaryType = BinaryType.LlamaCpp,
            VersionString = "b9611",
            NumericValue = 0x9611UL
        };

        var result = _checker.Compare(remote, local);

        Assert.True(result.HasUpdate);
        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal("b9611", result.CurrentVersion);
        Assert.Equal("b9612", result.LatestVersion);
    }

    [Fact]
    public void Compare_LlamaCpp_UpToDate_ReturnsCorrectResult()
    {
        var remote = new RemoteVersion
        {
            BinaryType = BinaryType.LlamaCpp,
            TagName = "b9611",
            NumericValue = 0x9611UL
        };

        var local = new LocalVersion
        {
            BinaryType = BinaryType.LlamaCpp,
            VersionString = "b9611",
            NumericValue = 0x9611UL
        };

        var result = _checker.Compare(remote, local);

        Assert.False(result.HasUpdate);
        Assert.Equal(UpdateStatus.UpToDate, result.Status);
    }

    [Fact]
    public void Compare_LlamaCpp_HexOrdering()
    {
        // b9620 > b9611 (hex comparison, not decimal)
        var remote = new RemoteVersion
        {
            BinaryType = BinaryType.LlamaCpp,
            TagName = "b9620",
            NumericValue = 0x9620UL
        };

        var local = new LocalVersion
        {
            BinaryType = BinaryType.LlamaCpp,
            VersionString = "b9611",
            NumericValue = 0x9611UL
        };

        var result = _checker.Compare(remote, local);

        Assert.True(result.HasUpdate);
        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
    }

    #endregion

    #region Error and unknown states

    [Fact]
    public void Compare_RemoteUnknown_ReturnsUnknownStatus()
    {
        var remote = new RemoteVersion
        {
            BinaryType = BinaryType.LlamaSwap,
            TagName = "v224",
            NumericValue = null // Simulates parse failure
        };

        var local = new LocalVersion
        {
            BinaryType = BinaryType.LlamaSwap,
            VersionString = "v223",
            NumericValue = 223UL
        };

        var result = _checker.Compare(remote, local);

        Assert.Equal(UpdateStatus.Unknown, result.Status);
    }

    [Fact]
    public void Compare_LocalUnknown_ReturnsUnknownStatus()
    {
        var remote = new RemoteVersion
        {
            BinaryType = BinaryType.LlamaSwap,
            TagName = "v224",
            NumericValue = 224UL
        };

        var local = new LocalVersion
        {
            BinaryType = BinaryType.LlamaSwap,
            VersionString = null,
            NumericValue = null
        };

        var result = _checker.Compare(remote, local);

        Assert.Equal(UpdateStatus.Unknown, result.Status);
    }

    [Fact]
    public void Compare_RemoteError_ReturnsErrorStatus()
    {
        var remote = new RemoteVersion
        {
            BinaryType = BinaryType.LlamaSwap,
            Status = UpdateStatus.Error,
            Error = "Network timeout"
        };

        var local = new LocalVersion
        {
            BinaryType = BinaryType.LlamaSwap,
            VersionString = "v224",
            NumericValue = 224UL
        };

        var result = _checker.Compare(remote, local);

        Assert.Equal(UpdateStatus.Error, result.Status);
        Assert.Contains("Network timeout", result.Error);
    }

    #endregion

    #region VersionComparison factory methods

    [Fact]
    public void VersionComparison_Success_UpToDate_CreatesCorrectObject()
    {
        var result = VersionComparison.Success(false, "v224", "v224", null);

        Assert.False(result.HasUpdate);
        Assert.Equal(UpdateStatus.UpToDate, result.Status);
        Assert.Equal("v224", result.CurrentVersion);
        Assert.Equal("v224", result.LatestVersion);
    }

    [Fact]
    public void VersionComparison_Success_UpdateAvailable_CreatesCorrectObject()
    {
        var result = VersionComparison.Success(true, "v224", "v225", "https://example.com/download");

        Assert.True(result.HasUpdate);
        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal("v224", result.CurrentVersion);
        Assert.Equal("v225", result.LatestVersion);
        Assert.Equal("https://example.com/download", result.DownloadUrl);
    }

    [Fact]
    public void VersionComparison_ErrorResult_CreatesCorrectObject()
    {
        var result = VersionComparison.ErrorResult("Network timeout");

        Assert.Equal(UpdateStatus.Error, result.Status);
        Assert.Equal("Network timeout", result.Error);
    }

    [Fact]
    public void VersionComparison_Unknown_CreatesCorrectObject()
    {
        var result = VersionComparison.Unknown("Could not detect version");

        Assert.Equal(UpdateStatus.Unknown, result.Status);
        Assert.Equal("Could not detect version", result.Error);
    }

    #endregion

    #region UpdateCheckResult

    [Fact]
    public void UpdateCheckResult_AnyUpdateAvailable_DetectsCorrectly()
    {
        var result = new UpdateCheckResult
        {
            LlamaSwap = VersionComparison.Success(false, "v224", "v224", null),
            LlamaCpp = VersionComparison.Success(true, "b9610", "b9611", null)
        };

        Assert.True(result.AnyUpdateAvailable);
    }

    [Fact]
    public void UpdateCheckResult_NoUpdatesAvailable_ReturnsFalse()
    {
        var result = new UpdateCheckResult
        {
            LlamaSwap = VersionComparison.Success(false, "v224", "v224", null),
            LlamaCpp = VersionComparison.Success(false, "b9611", "b9611", null)
        };

        Assert.False(result.AnyUpdateAvailable);
    }

    [Fact]
    public void UpdateCheckResult_BothHaveUpdates_ReturnsTrue()
    {
        var result = new UpdateCheckResult
        {
            LlamaSwap = VersionComparison.Success(true, "v223", "v224", null),
            LlamaCpp = VersionComparison.Success(true, "b9610", "b9611", null)
        };

        Assert.True(result.AnyUpdateAvailable);
    }

    [Fact]
    public void UpdateCheckResult_ContainsTimestamp()
    {
        var result = new UpdateCheckResult();
        Assert.NotEqual(default(DateTime), result.CheckedAt);
    }

    #endregion
}
