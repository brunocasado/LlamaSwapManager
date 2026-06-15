using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LlamaSwapManager.Models;

namespace LlamaSwapManager.Services;

/// <summary>
/// Service that checks for updates by querying GitHub API for the latest release.
/// Handles network errors gracefully — returns a no-op result on failure.
/// </summary>
public class VersionCheckService
{
    private readonly HttpClient _httpClient;
    private readonly string _owner;
    private readonly string _repo;
    private readonly string _currentVersion;
    private readonly TimeSpan _timeout;

    public VersionCheckService(
        HttpClient httpClient,
        string owner,
        string repo,
        string currentVersion,
        TimeSpan? timeout = null)
    {
        _httpClient = httpClient;
        _owner = owner;
        _repo = repo;
        _currentVersion = currentVersion;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Check for updates against the GitHub repository.
    /// Returns a VersionCheckResult with update info or error details.
    /// </summary>
    public async Task<VersionCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var result = new VersionCheckResult
        {
            CurrentVersion = _currentVersion
        };

        try
        {
            var url = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                result.Error = $"Repository {_owner}/{_repo} has no releases";
                return result;
            }

            if (!response.IsSuccessStatusCode)
            {
                result.Error = $"GitHub API returned {response.StatusCode}";
                return result;
            }

            using var json = await response.Content.ReadAsStreamAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString();
            var htmlUrl = root.GetProperty("html_url").GetString();
            var assetsUrl = root.GetProperty("assets_url").GetString();

            if (string.IsNullOrEmpty(tagName))
            {
                result.Error = "No tag_name in release response";
                return result;
            }

            result.LatestVersion = tagName;
            result.ReleaseTag = tagName;
            result.DownloadUrl = htmlUrl;
            result.AssetsUrl = assetsUrl;

            // Compare versions
            result.HasUpdate = VersionComparer.HasUpdate(_currentVersion, tagName);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            // User cancelled — treat as no update
            result.HasUpdate = false;
        }
        catch (TaskCanceledException)
        {
            result.Error = "Request timed out";
        }
        catch (Exception ex)
        {
            // Network error, JSON parse error, etc. — no-op
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Check for updates synchronously (wraps async with Task.Run to avoid context capture).
    /// </summary>
    public VersionCheckResult Check()
    {
        return Task.Run(async () => await CheckAsync(CancellationToken.None)).Result;
    }
}
