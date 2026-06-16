using System;

namespace LlamaSwapManager.Services;

/// <summary>
/// Utility for parsing and comparing llama-swap and llama.cpp version strings.
///
/// llama-swap:  v + integer (e.g. v223, v224)
/// llama.cpp:   b + decimal build number (e.g. b9660, b9659)
/// </summary>
public static class VersionComparer
{
    /// <summary>
    /// Parse a version string into its numeric components.
    /// Returns null if the format is unrecognized.
    /// </summary>
    public static VersionInfo? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var trimmed = version.Trim();

        // llama-swap: v + integer
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(trimmed[1..], out var vNum))
        {
            return new VersionInfo { Type = VersionType.LlamaSwap, NumericValue = (ulong)vNum, Raw = trimmed };
        }

        // llama.cpp: b + decimal build number (e.g., b9660, b9659, b9553)
        if (trimmed.StartsWith("b", StringComparison.OrdinalIgnoreCase)
            && trimmed.Length >= 2)
        {
            var decimalPart = trimmed[1..];
            if (ulong.TryParse(decimalPart, out var bNum))
            {
                return new VersionInfo { Type = VersionType.LlamaCpp, NumericValue = bNum, Raw = trimmed };
            }
        }

        // Fallback: try to extract a pure integer (for edge cases)
        if (int.TryParse(trimmed, out var pureInt))
        {
            return new VersionInfo { Type = VersionType.Unknown, NumericValue = (ulong)pureInt, Raw = trimmed };
        }

        return null;
    }

    /// <summary>
    /// Compare two version strings. Returns:
    ///   -1 if current < latest
    ///    0 if current == latest
    ///   +1 if current > latest
    /// </summary>
    public static int Compare(string? current, string? latest)
    {
        var v1 = Parse(current);
        var v2 = Parse(latest);

        if (v1 is null && v2 is null)
            return 0;
        if (v1 is null)
            return -1;
        if (v2 is null)
            return 1;

        // Different version types are not comparable — treat as equal
        if (v1.Type != v2.Type)
            return 0;

        return v1.NumericValue.CompareTo(v2.NumericValue);
    }

    /// <summary>
    /// Check if a newer version is available.
    /// Returns false if either version is unrecognized or they are equal.
    /// </summary>
    public static bool HasUpdate(string? currentVersion, string? latestVersion)
    {
        return Compare(currentVersion, latestVersion) < 0;
    }

    /// <summary>
    /// Extract the version number from a GitHub tag (removes leading 'v' or 'b').
    /// </summary>
    public static string StripPrefix(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return "";

        var trimmed = tag.Trim();
        if (trimmed.Length > 1 && (trimmed[0] == 'v' || trimmed[0] == 'V'
                                    || trimmed[0] == 'b' || trimmed[0] == 'B'))
        {
            return trimmed.Substring(1);
        }
        return trimmed;
    }
}

public enum VersionType
{
    Unknown,
    LlamaSwap,
    LlamaCpp
}

public record VersionInfo
{
    public VersionType Type { get; init; }
    public ulong NumericValue { get; init; }
    public string Raw { get; init; } = "";
}
