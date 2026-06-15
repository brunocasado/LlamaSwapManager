using System;

namespace LlamaSwapManager.Services;

/// <summary>
/// Represents a version detected from a remote source (GitHub release).
/// </summary>
public class RemoteVersion
{
    /// <summary>
    /// The binary type this version belongs to.
    /// </summary>
    public BinaryType BinaryType { get; set; }

    /// <summary>
    /// The raw version string from GitHub (e.g., "v224" or "b9611").
    /// </summary>
    public string? TagName { get; set; }

    /// <summary>
    /// Numeric representation for comparison. llama-swap: parsed int from "v224" -> 224.
    /// llama.cpp: parsed uint from hex digits in "b9611" -> 0x9611.
    /// Null if the tag cannot be parsed.
    /// </summary>
    public ulong? NumericValue { get; set; }

    /// <summary>
    /// Download URL for the latest release asset, if available.
    /// </summary>
    public string? DownloadUrl { get; set; }

    /// <summary>
    /// SHA-256 checksum of the release asset, if available.
    /// </summary>
    public string? Checksum { get; set; }

    /// <summary>
    /// Status of this remote version entry.
    /// </summary>
    public UpdateStatus Status { get; set; } = UpdateStatus.Unknown;

    /// <summary>
    /// Error message if fetching this version failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Whether this version could be successfully parsed.
    /// </summary>
    public bool IsKnown => TagName is not null && NumericValue.HasValue;
}

/// <summary>
/// Represents a locally installed binary version.
/// </summary>
public class LocalVersion
{
    /// <summary>
    /// The binary type this version belongs to.
    /// </summary>
    public BinaryType BinaryType { get; set; }

    /// <summary>
    /// The raw version string detected locally.
    /// For llama-swap: output of `llama-swap --version` or API /api/version.
    /// For llama.cpp: parsed from `llama-server --help` or a stored marker file.
    /// </summary>
    public string? VersionString { get; set; }

    /// <summary>
    /// Numeric representation for comparison. Same format as RemoteVersion.
    /// </summary>
    public ulong? NumericValue { get; set; }

    /// <summary>
    /// Path to the installed binary (or directory for llama.cpp).
    /// </summary>
    public string? BinaryPath { get; set; }

    /// <summary>
    /// Status of the local version detection.
    /// </summary>
    public UpdateStatus Status { get; set; } = UpdateStatus.Unknown;

    /// <summary>
    /// Whether the local version could be successfully detected.
    /// </summary>
    public bool IsKnown => VersionString is not null && NumericValue.HasValue;
}

/// <summary>
/// Result of comparing a remote version against a local version.
/// </summary>
public class VersionComparison
{
    /// <summary>
    /// Whether an update is available (remote version is newer).
    /// </summary>
    public bool HasUpdate { get; set; }

    /// <summary>
    /// The current local version string.
    /// </summary>
    public string? CurrentVersion { get; set; }

    /// <summary>
    /// The latest remote version string.
    /// </summary>
    public string? LatestVersion { get; set; }

    /// <summary>
    /// Download URL for the update, if available.
    /// </summary>
    public string? DownloadUrl { get; set; }

    /// <summary>
    /// Status of the comparison.
    /// </summary>
    public UpdateStatus Status { get; set; }

    /// <summary>
    /// Error message if the comparison failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Create a successful comparison result.
    /// </summary>
    public static VersionComparison Success(bool hasUpdate, string? currentVersion, string? latestVersion, string? downloadUrl)
    {
        return new VersionComparison
        {
            HasUpdate = hasUpdate,
            CurrentVersion = currentVersion,
            LatestVersion = latestVersion,
            DownloadUrl = downloadUrl,
            Status = hasUpdate ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate
        };
    }

    /// <summary>
    /// Create an error result.
    /// </summary>
    public static VersionComparison ErrorResult(string errorMessage)
    {
        return new VersionComparison
        {
            Status = UpdateStatus.Error,
            Error = errorMessage
        };
    }

    /// <summary>
    /// Create an unknown result (version couldn't be parsed).
    /// </summary>
    public static VersionComparison Unknown(string reason)
    {
        return new VersionComparison
        {
            Status = UpdateStatus.Unknown,
            Error = reason
        };
    }
}

/// <summary>
/// Overall result of checking for updates across all tracked binaries.
/// </summary>
public class UpdateCheckResult
{
    /// <summary>
    /// llama-swap specific results.
    /// </summary>
    public VersionComparison LlamaSwap { get; set; } = new();

    /// <summary>
    /// llama.cpp specific results.
    /// </summary>
    public VersionComparison LlamaCpp { get; set; } = new();

    /// <summary>
    /// Whether any binary has an update available.
    /// </summary>
    public bool AnyUpdateAvailable => LlamaSwap.HasUpdate || LlamaCpp.HasUpdate;

    /// <summary>
    /// Timestamp when this check was performed.
    /// </summary>
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Any error message from the check process.
    /// </summary>
    public string? Error { get; set; }
}

/// <summary>
/// Status of a version comparison.
/// </summary>
public enum UpdateStatus
{
    /// <summary>
    /// Local version matches the latest remote version.
    /// </summary>
    UpToDate,

    /// <summary>
    /// A newer version is available remotely.
    /// </summary>
    UpdateAvailable,

    /// <summary>
    /// Version could not be determined (unparseable tag, missing binary, etc.).
    /// </summary>
    Unknown,

    /// <summary>
    /// An error occurred during the check (network failure, API error, etc.).
    /// </summary>
    Error
}

/// <summary>
/// Which binary is being checked.
/// </summary>
public enum BinaryType
{
    LlamaSwap,
    LlamaCpp
}
