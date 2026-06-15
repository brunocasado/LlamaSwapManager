namespace TestVersion;

/// <summary>
/// Result of a version check against a GitHub repository.
/// </summary>
public class VersionCheckResult
{
    /// <summary>
    /// Whether a newer version is available.
    /// </summary>
    public bool HasUpdate { get; set; }

    /// <summary>
    /// The current version string (e.g. "v223" or "b9610").
    /// </summary>
    public string CurrentVersion { get; set; } = "";

    /// <summary>
    /// The latest version string from GitHub (e.g. "v224" or "b9616").
    /// </summary>
    public string LatestVersion { get; set; } = "";

    /// <summary>
    /// URL to the GitHub release page.
    /// </summary>
    public string? DownloadUrl { get; set; }

    /// <summary>
    /// URL to the latest release assets page.
    /// </summary>
    public string? AssetsUrl { get; set; }

    /// <summary>
    /// The GitHub release tag name (e.g. "v224").
    /// </summary>
    public string? ReleaseTag { get; set; }

    /// <summary>
    /// Error message if the check failed (null on success).
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Whether the check was performed successfully.
    /// </summary>
    public bool IsSuccess => Error is null;
}
