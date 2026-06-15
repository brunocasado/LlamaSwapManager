using LlamaSwapManager.Services;

namespace LlamaSwapManager.Tests;

public class VersionComparerTests
{
    // --- Parse tests ---

    [Fact]
    public void Parse_LlamaSwapVersion_ReturnsCorrectTypeAndValue()
    {
        var result = VersionComparer.Parse("v224");
        Assert.NotNull(result);
        Assert.Equal(VersionType.LlamaSwap, result.Type);
        Assert.Equal(224ul, result.NumericValue);
        Assert.Equal("v224", result.Raw);
    }

    [Fact]
    public void Parse_LlamaCppVersion_ReturnsCorrectTypeAndValue()
    {
        var result = VersionComparer.Parse("b9616");
        Assert.NotNull(result);
        Assert.Equal(VersionType.LlamaCpp, result.Type);
        Assert.Equal(0x9616ul, result.NumericValue);
        Assert.Equal("b9616", result.Raw);
    }

    [Fact]
    public void Parse_SingleDigitLlamaSwap_ReturnsCorrectValue()
    {
        var result = VersionComparer.Parse("v1");
        Assert.NotNull(result);
        Assert.Equal(VersionType.LlamaSwap, result.Type);
        Assert.Equal(1ul, result.NumericValue);
    }

    [Fact]
    public void Parse_LlamaCppWithLeadingZeros_ReturnsCorrectHexValue()
    {
        var result = VersionComparer.Parse("b0001");
        Assert.NotNull(result);
        Assert.Equal(VersionType.LlamaCpp, result.Type);
        Assert.Equal(1ul, result.NumericValue);
    }

    [Fact]
    public void Parse_Null_ReturnsNull()
    {
        Assert.Null(VersionComparer.Parse(null!));
    }

    [Fact]
    public void Parse_EmptyString_ReturnsNull()
    {
        Assert.Null(VersionComparer.Parse(""));
    }

    [Fact]
    public void Parse_InvalidString_ReturnsNull()
    {
        Assert.Null(VersionComparer.Parse("invalid"));
    }

    [Fact]
    public void Parse_WhitespaceTrimmed()
    {
        var result = VersionComparer.Parse("  v224  ");
        Assert.NotNull(result);
        Assert.Equal(224ul, result.NumericValue);
        Assert.Equal("v224", result.Raw);
    }

    [Fact]
    public void Parse_UppercasePrefix_LlamaSwap()
    {
        var result = VersionComparer.Parse("V224");
        Assert.NotNull(result);
        Assert.Equal(VersionType.LlamaSwap, result.Type);
        Assert.Equal(224ul, result.NumericValue);
    }

    [Fact]
    public void Parse_UppercasePrefix_LlamaCpp()
    {
        var result = VersionComparer.Parse("B9616");
        Assert.NotNull(result);
        Assert.Equal(VersionType.LlamaCpp, result.Type);
        Assert.Equal(0x9616ul, result.NumericValue);
    }

    // --- Compare tests ---

    [Fact]
    public void Compare_SameLlamaSwapVersion_ReturnsZero()
    {
        Assert.Equal(0, VersionComparer.Compare("v224", "v224"));
    }

    [Fact]
    public void Compare_SameLlamaCppVersion_ReturnsZero()
    {
        Assert.Equal(0, VersionComparer.Compare("b9616", "b9616"));
    }

    [Fact]
    public void Compare_LlamaSwapNewer_ReturnsPositive()
    {
        Assert.Equal(1, VersionComparer.Compare("v224", "v223"));
    }

    [Fact]
    public void Compare_LlamaSwapOlder_ReturnsNegative()
    {
        Assert.Equal(-1, VersionComparer.Compare("v223", "v224"));
    }

    [Fact]
    public void Compare_LlamaSwapLargeGap()
    {
        Assert.Equal(-1, VersionComparer.Compare("v1", "v224"));
        Assert.Equal(1, VersionComparer.Compare("v224", "v1"));
    }

    [Fact]
    public void Compare_LlamaSwapSingleVsDoubleDigit()
    {
        Assert.Equal(-1, VersionComparer.Compare("v9", "v10"));
        Assert.Equal(1, VersionComparer.Compare("v10", "v9"));
    }

    [Fact]
    public void Compare_LlamaCppNewer_ReturnsPositive()
    {
        Assert.Equal(1, VersionComparer.Compare("b9616", "b9610"));
    }

    [Fact]
    public void Compare_LlamaCppOlder_ReturnsNegative()
    {
        Assert.Equal(-1, VersionComparer.Compare("b9610", "b9616"));
    }

    [Fact]
    public void Compare_LlamaCppHexBoundary()
    {
        Assert.Equal(1, VersionComparer.Compare("ba000", "b9999"));
        Assert.Equal(-1, VersionComparer.Compare("b9999", "ba000"));
    }

    [Fact]
    public void Compare_DifferentTypes_ReturnsZero()
    {
        Assert.Equal(0, VersionComparer.Compare("v224", "b9616"));
        Assert.Equal(0, VersionComparer.Compare("b9616", "v224"));
    }

    [Fact]
    public void Compare_NullBoth_ReturnsZero()
    {
        Assert.Equal(0, VersionComparer.Compare(null, null));
    }

    [Fact]
    public void Compare_NullCurrent_ReturnsNegative()
    {
        Assert.Equal(-1, VersionComparer.Compare(null, "v224"));
    }

    [Fact]
    public void Compare_NullLatest_ReturnsPositive()
    {
        Assert.Equal(1, VersionComparer.Compare("v224", null));
    }

    [Fact]
    public void Compare_ZeroVersions()
    {
        Assert.Equal(0, VersionComparer.Compare("v0", "v0"));
        Assert.Equal(-1, VersionComparer.Compare("v0", "v1"));
    }

    [Fact]
    public void Compare_VeryLargeVersionNumbers()
    {
        Assert.Equal(-1, VersionComparer.Compare("v1", "v999999"));
        Assert.Equal(1, VersionComparer.Compare("v999999", "v1"));
    }

    // --- HasUpdate tests ---

    [Fact]
    public void HasUpdate_NewerVersionAvailable_ReturnsTrue()
    {
        Assert.True(VersionComparer.HasUpdate("v223", "v224"));
    }

    [Fact]
    public void HasUpdate_SameVersion_ReturnsFalse()
    {
        Assert.False(VersionComparer.HasUpdate("v224", "v224"));
    }

    [Fact]
    public void HasUpdate_OlderVersion_ReturnsFalse()
    {
        Assert.False(VersionComparer.HasUpdate("v224", "v223"));
    }

    [Fact]
    public void HasUpdate_NullCurrent_ReturnsTrue()
    {
        // Compare(null, "v224") returns -1, so HasUpdate returns true (conservative)
        Assert.True(VersionComparer.HasUpdate(null, "v224"));
    }

    [Fact]
    public void HasUpdate_NullLatest_ReturnsFalse()
    {
        Assert.False(VersionComparer.HasUpdate("v224", null));
    }

    [Fact]
    public void HasUpdate_DifferentTypes_ReturnsFalse()
    {
        Assert.False(VersionComparer.HasUpdate("v224", "b9616"));
    }

    // --- StripPrefix tests ---

    [Fact]
    public void StripPrefix_LlamaSwap_RemovesV()
    {
        Assert.Equal("224", VersionComparer.StripPrefix("v224"));
        Assert.Equal("224", VersionComparer.StripPrefix("V224"));
    }

    [Fact]
    public void StripPrefix_LlamaCpp_RemovesB()
    {
        Assert.Equal("9616", VersionComparer.StripPrefix("b9616"));
        Assert.Equal("9616", VersionComparer.StripPrefix("B9616"));
    }

    [Fact]
    public void StripPrefix_NoPrefix_ReturnsAsIs()
    {
        Assert.Equal("224", VersionComparer.StripPrefix("224"));
    }

    [Fact]
    public void StripPrefix_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Equal("", VersionComparer.StripPrefix(""));
        Assert.Equal("", VersionComparer.StripPrefix(null!));
    }
}
