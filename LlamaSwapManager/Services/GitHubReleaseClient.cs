using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

internal sealed class GitHubReleaseClient
{
    private static readonly Uri LatestReleaseUri = new(
        "https://api.github.com/repos/ggml-org/llama.cpp/releases/latest");

    private readonly HttpClient _http;
    private readonly Action<string>? _log;

    public GitHubReleaseClient(HttpClient http, Action<string>? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log;
    }

    public async Task<JsonElement?> GetLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(LatestReleaseUri, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log?.Invoke(
                    $"[llama.cpp] GitHub API error: {(int)response.StatusCode} {response.StatusCode}");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: ct);
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            _log?.Invoke("[llama.cpp] GitHub request timed out");
            return null;
        }
        catch (HttpRequestException ex)
        {
            _log?.Invoke($"[llama.cpp] Network error fetching release: {ex.Message}");
            return null;
        }
        catch (JsonException ex)
        {
            _log?.Invoke($"[llama.cpp] Invalid GitHub release response: {ex.Message}");
            return null;
        }
    }
}
