using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LlamaSwapManager.Models;

namespace LlamaSwapManager.Services;

/// <summary>
/// Version check service for llama.cpp (ggml-org/llama.cpp).
/// Queries GitHub API for the latest release, compares with the local version,
/// and returns a VersionCheckResult with update information.
///
/// Network errors are handled gracefully — returns a no-op result on failure.
/// </summary>
public class LlamaCppVersionCheckService
{
    private readonly VersionCheckService _inner;
    private const string Owner = "ggml-org";
    private const string Repo = "llama.cpp";

    /// <summary>
    /// Creates a new llama.cpp version check service.
    /// </summary>
    /// <param name="httpClient">HttpClient to use for API requests.</param>
    /// <param name="currentVersion">Current installed llama.cpp version (e.g., "b9610").</param>
    /// <param name="timeout">Request timeout. Defaults to 10 seconds.</param>
    public LlamaCppVersionCheckService(
        HttpClient httpClient,
        string currentVersion,
        TimeSpan? timeout = null)
    {
        _inner = new VersionCheckService(httpClient, Owner, Repo, currentVersion, timeout);
    }

    /// <summary>
    /// Check for updates asynchronously.
    /// </summary>
    public Task<VersionCheckResult> CheckAsync(CancellationToken ct = default)
        => _inner.CheckAsync(ct);

    /// <summary>
    /// Check for updates synchronously.
    /// </summary>
    public VersionCheckResult Check()
        => _inner.Check();
}
