using System;
using LlamaSwapManager.Services;
using Xunit;

namespace LlamaSwapManager.Tests;

/// <summary>
/// Tests for version parsing logic in VersionChecker.
/// </summary>
public class VersionParserTests
{
    #region llama-swap version parsing

    [Fact]
    public void ParseLlamaSwapVersion_ValidVersion_ReturnsCorrectNumber()
    {
        // v224 -> 224
        Assert.Equal(224UL, VersionChecker.ParseLlamaSwapVersion("v224"));
    }

    [Fact]
    public void ParseLlamaSwapVersion_CaseInsensitive()
    {
        // Both "v224" and "V224" should work
        Assert.Equal(224UL, VersionChecker.ParseLlamaSwapVersion("v224"));
        Assert.Equal(224UL, VersionChecker.ParseLlamaSwapVersion("V224"));
    }

    [Fact]
    public void ParseLlamaSwapVersion_EdgeCases()
    {
        // v1
        Assert.Equal(1UL, VersionChecker.ParseLlamaSwapVersion("v1"));
        // v9999
        Assert.Equal(9999UL, VersionChecker.ParseLlamaSwapVersion("v9999"));
        // v0
        Assert.Equal(0UL, VersionChecker.ParseLlamaSwapVersion("v0"));
    }

    [Fact]
    public void ParseLlamaSwapVersion_EmptyString_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => VersionChecker.ParseLlamaSwapVersion(""));
        Assert.Throws<ArgumentException>(() => VersionChecker.ParseLlamaSwapVersion(null!));
    }

    [Fact]
    public void ParseLlamaSwapVersion_InvalidFormat_ThrowsFormatException()
    {
        // Missing "v" prefix
        Assert.Throws<FormatException>(() => VersionChecker.ParseLlamaSwapVersion("224"));
        // Non-numeric after "v"
        Assert.Throws<FormatException>(() => VersionChecker.ParseLlamaSwapVersion("vabc"));
        // Just "v"
        Assert.Throws<FormatException>(() => VersionChecker.ParseLlamaSwapVersion("v"));
    }

    #endregion

    #region llama.cpp version parsing

    [Fact]
    public void ParseLlamaCppVersion_ValidVersion_ReturnsCorrectHex()
    {
        // b9611 -> 0x9611 = 38417
        Assert.Equal(0x9611UL, VersionChecker.ParseLlamaCppVersion("b9611"));
    }

    [Fact]
    public void ParseLlamaCppVersion_CaseInsensitive()
    {
        Assert.Equal(0x9611UL, VersionChecker.ParseLlamaCppVersion("b9611"));
        Assert.Equal(0x9611UL, VersionChecker.ParseLlamaCppVersion("B9611"));
        Assert.Equal(0x9611UL, VersionChecker.ParseLlamaCppVersion("b9611"));
    }

    [Fact]
    public void ParseLlamaCppVersion_HexValues()
    {
        // b0000 -> 0
        Assert.Equal(0UL, VersionChecker.ParseLlamaCppVersion("b0000"));
        // bffff -> 65535
        Assert.Equal(0xFFFFUL, VersionChecker.ParseLlamaCppVersion("bffff"));
        // b1000 -> 4096
        Assert.Equal(0x1000UL, VersionChecker.ParseLlamaCppVersion("b1000"));
    }

    [Fact]
    public void ParseLlamaCppVersion_EmptyString_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => VersionChecker.ParseLlamaCppVersion(""));
        Assert.Throws<ArgumentException>(() => VersionChecker.ParseLlamaCppVersion(null!));
    }

    [Fact]
    public void ParseLlamaCppVersion_InvalidFormat_ThrowsFormatException()
    {
        // Missing "b" prefix
        Assert.Throws<FormatException>(() => VersionChecker.ParseLlamaCppVersion("9611"));
        // Non-hex characters
        Assert.Throws<FormatException>(() => VersionChecker.ParseLlamaCppVersion("bgggg"));
        // Too short (less than 6 chars total)
        Assert.Throws<FormatException>(() => VersionChecker.ParseLlamaCppVersion("b96"));
        // Just "b"
        Assert.Throws<FormatException>(() => VersionChecker.ParseLlamaCppVersion("b"));
    }

    #endregion
}
