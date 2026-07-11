using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

internal sealed class GitHubReleaseClient
{
    private static readonly TimeSpan DefaultCacheLifetime = TimeSpan.FromHours(4);

    private readonly HttpClient _http;
    private readonly Uri _latestReleaseUri;
    private readonly string _cachePath;
    private readonly TimeSpan _cacheLifetime;
    private readonly Action<string>? _log;

    public GitHubReleaseClient(
        HttpClient http,
        string repository,
        string cacheDirectory,
        Action<string>? log = null,
        TimeSpan? cacheLifetime = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        _latestReleaseUri = new Uri(
            $"https://api.github.com/repos/{repository}/releases/latest");
        var cacheFileName = repository.Replace('/', '-') + "-latest-release.json";
        _cachePath = Path.Combine(cacheDirectory, cacheFileName);
        _cacheLifetime = cacheLifetime ?? DefaultCacheLifetime;
        _log = log;
    }

    public async Task<JsonElement?> GetLatestReleaseAsync(CancellationToken ct)
    {
        var cache = await ReadCacheAsync(ct);
        var now = DateTimeOffset.UtcNow;

        if (cache is not null && now - cache.FetchedAtUtc < _cacheLifetime)
        {
            _log?.Invoke("[llama.cpp] Using cached GitHub release information");
            return ParseCachedRelease(cache);
        }

        if (cache?.RetryAfterUtc is { } retryAfter && retryAfter > now)
        {
            _log?.Invoke($"[llama.cpp] GitHub retry deferred until {retryAfter:O}; using cached release");
            return ParseCachedRelease(cache);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _latestReleaseUri);
            if (!string.IsNullOrWhiteSpace(cache?.ETag) &&
                EntityTagHeaderValue.TryParse(cache.ETag, out var etag))
            {
                request.Headers.IfNoneMatch.Add(etag);
            }

            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            if (response.StatusCode == HttpStatusCode.NotModified && cache is not null)
            {
                cache.FetchedAtUtc = now;
                cache.RetryAfterUtc = null;
                await WriteCacheAsync(cache, ct);
                _log?.Invoke("[llama.cpp] GitHub release unchanged; refreshed local cache");
                return ParseCachedRelease(cache);
            }

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                using var document = JsonDocument.Parse(json);

                var updatedCache = new ReleaseCacheEntry
                {
                    ETag = response.Headers.ETag?.ToString(),
                    FetchedAtUtc = now,
                    RetryAfterUtc = null,
                    Json = json
                };

                await WriteCacheAsync(updatedCache, ct);
                return document.RootElement.Clone();
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                var retryAt = ResolveRetryAfter(response, now);
                if (cache is not null)
                {
                    cache.RetryAfterUtc = retryAt;
                    await WriteCacheAsync(cache, ct);
                    _log?.Invoke(
                        $"[llama.cpp] GitHub rate limit reached; using cached release until {retryAt:O}");
                    return ParseCachedRelease(cache);
                }
            }

            _log?.Invoke(
                $"[llama.cpp] GitHub API error: {(int)response.StatusCode} {response.StatusCode}");
            return ParseCachedRelease(cache);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            _log?.Invoke("[llama.cpp] GitHub request timed out; using cached release");
            return ParseCachedRelease(cache);
        }
        catch (HttpRequestException ex)
        {
            _log?.Invoke($"[llama.cpp] Network error fetching release: {ex.Message}; using cache");
            return ParseCachedRelease(cache);
        }
        catch (JsonException ex)
        {
            _log?.Invoke($"[llama.cpp] Invalid GitHub release response: {ex.Message}");
            return ParseCachedRelease(cache);
        }
    }

    private async Task<ReleaseCacheEntry?> ReadCacheAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_cachePath))
                return null;

            await using var stream = File.OpenRead(_cachePath);
            return await JsonSerializer.DeserializeAsync<ReleaseCacheEntry>(stream, cancellationToken: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log?.Invoke($"[llama.cpp] Release cache read warning: {ex.Message}");
            return null;
        }
    }

    private async Task WriteCacheAsync(ReleaseCacheEntry cache, CancellationToken ct)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath)!;
            Directory.CreateDirectory(directory);
            var tempPath = _cachePath + ".tmp";

            await using (var stream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, cache, cancellationToken: ct);
            }

            File.Move(tempPath, _cachePath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"[llama.cpp] Release cache write warning: {ex.Message}");
        }
    }

    private JsonElement? ParseCachedRelease(ReleaseCacheEntry? cache)
    {
        if (string.IsNullOrWhiteSpace(cache?.Json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(cache.Json);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            _log?.Invoke($"[llama.cpp] Cached release is invalid: {ex.Message}");
            return null;
        }
    }

    private static DateTimeOffset ResolveRetryAfter(
        HttpResponseMessage response,
        DateTimeOffset now)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return now.Add(delta);

        if (response.Headers.RetryAfter?.Date is { } date)
            return date;

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values))
        {
            foreach (var value in values)
            {
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
                    return DateTimeOffset.FromUnixTimeSeconds(epoch);
            }
        }

        return now.AddMinutes(15);
    }

    private sealed class ReleaseCacheEntry
    {
        public string? ETag { get; set; }
        public DateTimeOffset FetchedAtUtc { get; set; }
        public DateTimeOffset? RetryAfterUtc { get; set; }
        public string Json { get; set; } = string.Empty;
    }
}
