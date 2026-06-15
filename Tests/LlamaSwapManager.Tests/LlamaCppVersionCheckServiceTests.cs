using LlamaSwapManager.Models;
using LlamaSwapManager.Services;
using System.Net;
using System.Text;

namespace LlamaSwapManager.Tests;

public class LlamaCppVersionCheckServiceTests
{
    private static readonly string LatestLlamaCppReleaseJson = """
    {
        "tag_name": "b9616",
        "html_url": "https://github.com/ggml-org/llama.cpp/releases/tag/b9616",
        "assets_url": "https://api.github.com/repos/ggml-org/llama.cpp/releases/789012/assets"
    }
    """;

    private static readonly string OldLlamaCppReleaseJson = """
    {
        "tag_name": "b9610",
        "html_url": "https://github.com/ggml-org/llama.cpp/releases/tag/b9610",
        "assets_url": "https://api.github.com/repos/ggml-org/llama.cpp/releases/789010/assets"
    }
    """;

    private static HttpMessageHandler CreateMockHandler(HttpStatusCode statusCode, string? content = null, TimeSpan? delay = null)
    {
        return new MockHttpMessageHandler(statusCode, content, delay);
    }

    private static LlamaCppVersionCheckService CreateService(
        HttpMessageHandler handler,
        string currentVersion = "b9610")
    {
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        return new LlamaCppVersionCheckService(httpClient, currentVersion);
    }

    // --- Happy path: update available ---

    [Fact]
    public async Task CheckAsync_NewerVersionAvailable_ReturnsHasUpdateTrue()
    {
        var service = CreateService(CreateMockHandler(HttpStatusCode.OK, LatestLlamaCppReleaseJson), "b9610");
        var result = await service.CheckAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.HasUpdate);
        Assert.Equal("b9610", result.CurrentVersion);
        Assert.Equal("b9616", result.LatestVersion);
        Assert.Equal("b9616", result.ReleaseTag);
        Assert.Equal("https://github.com/ggml-org/llama.cpp/releases/tag/b9616", result.DownloadUrl);
        Assert.NotNull(result.AssetsUrl);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CheckAsync_UpToDate_ReturnsHasUpdateFalse()
    {
        var service = CreateService(CreateMockHandler(HttpStatusCode.OK, LatestLlamaCppReleaseJson), "b9616");
        var result = await service.CheckAsync();

        Assert.True(result.IsSuccess);
        Assert.False(result.HasUpdate);
        Assert.Equal("b9616", result.LatestVersion);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CheckAsync_OlderVersion_ReturnsHasUpdateFalse()
    {
        var service = CreateService(CreateMockHandler(HttpStatusCode.OK, OldLlamaCppReleaseJson), "b9616");
        var result = await service.CheckAsync();

        Assert.True(result.IsSuccess);
        Assert.False(result.HasUpdate);
        Assert.Equal("b9610", result.LatestVersion);
    }

    // --- Error handling: HTTP status codes ---

    [Fact]
    public async Task CheckAsync_NotFound_ReturnsError()
    {
        var service = CreateService(CreateMockHandler(HttpStatusCode.NotFound));
        var result = await service.CheckAsync();

        Assert.False(result.IsSuccess);
        Assert.False(result.HasUpdate);
        Assert.NotNull(result.Error);
        Assert.Contains("no releases", result.Error!);
    }

    [Fact]
    public async Task CheckAsync_Forbidden_ReturnsError()
    {
        var service = CreateService(CreateMockHandler(HttpStatusCode.Forbidden));
        var result = await service.CheckAsync();

        Assert.False(result.IsSuccess);
        Assert.False(result.HasUpdate);
        Assert.NotNull(result.Error);
        Assert.Contains("Forbidden", result.Error!);
    }

    [Fact]
    public async Task CheckAsync_ServerError_ReturnsError()
    {
        var service = CreateService(CreateMockHandler(HttpStatusCode.InternalServerError));
        var result = await service.CheckAsync();

        Assert.False(result.IsSuccess);
        Assert.False(result.HasUpdate);
        Assert.NotNull(result.Error);
        Assert.Contains("InternalServerError", result.Error!);
    }

    [Fact]
    public async Task CheckAsync_TooManyRequests_ReturnsError()
    {
        var service = CreateService(CreateMockHandler(HttpStatusCode.TooManyRequests));
        var result = await service.CheckAsync();

        Assert.False(result.IsSuccess);
        Assert.False(result.HasUpdate);
        Assert.NotNull(result.Error);
        Assert.Contains("TooManyRequests", result.Error!);
    }

    // --- Error handling: malformed response ---

    [Fact]
    public async Task CheckAsync_MissingTagName_ReturnsError()
    {
        var noTagJson = """
        {
            "html_url": "https://github.com/ggml-org/llama.cpp/releases/tag/b9616",
            "assets_url": "https://api.github.com/repos/ggml-org/llama.cpp/releases/789012/assets"
        }
        """;
        var service = CreateService(CreateMockHandler(HttpStatusCode.OK, noTagJson));
        var result = await service.CheckAsync();

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task CheckAsync_EmptyTagName_ReturnsError()
    {
        var emptyTagJson = """
        {
            "tag_name": "",
            "html_url": "https://github.com/ggml-org/llama.cpp/releases/tag/",
            "assets_url": "https://api.github.com/repos/ggml-org/llama.cpp/releases/789012/assets"
        }
        """;
        var service = CreateService(CreateMockHandler(HttpStatusCode.OK, emptyTagJson));
        var result = await service.CheckAsync();

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task CheckAsync_MalformedJson_ReturnsError()
    {
        var service = CreateService(CreateMockHandler(HttpStatusCode.OK, "not valid json{{{"));
        var result = await service.CheckAsync();

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    // --- Error handling: network errors ---

    [Fact]
    public async Task CheckAsync_Timeout_ReturnsError()
    {
        var service = CreateService(CreateMockHandler(HttpStatusCode.OK, LatestLlamaCppReleaseJson, TimeSpan.FromSeconds(30)), "b9610");
        var result = await service.CheckAsync();

        Assert.False(result.IsSuccess);
        Assert.False(result.HasUpdate);
        Assert.NotNull(result.Error);
        Assert.Contains("timed out", result.Error!.ToLower());
    }

    [Fact]
    public async Task CheckAsync_Cancellation_ReturnsNoUpdate()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = CreateService(CreateMockHandler(HttpStatusCode.OK, LatestLlamaCppReleaseJson), "b9610");
        var result = await service.CheckAsync(cts.Token);

        Assert.True(result.IsSuccess);
        Assert.False(result.HasUpdate);
    }

    // --- Synchronous Check method ---

    [Fact]
    public void Check_Synchronous_ReturnsResult()
    {
        var service = CreateService(CreateMockHandler(HttpStatusCode.OK, LatestLlamaCppReleaseJson), "b9610");
        var result = service.Check();

        Assert.True(result.HasUpdate);
        Assert.Equal("b9616", result.LatestVersion);
    }

    // --- Dependency injection friendly ---

    [Fact]
    public void Constructor_SetsDefaults()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, LatestLlamaCppReleaseJson);
        var httpClient = new HttpClient(handler);
        var service = new LlamaCppVersionCheckService(httpClient, "b9600");

        var result = service.Check();
        Assert.True(result.HasUpdate);
    }

    // --- Hex version comparison edge cases ---

    [Fact]
    public async Task CheckAsync_HexVersionOrdering_Correct()
    {
        // b9620 > b9611 (hex comparison)
        var newerJson = """
        {
            "tag_name": "b9620",
            "html_url": "https://github.com/ggml-org/llama.cpp/releases/tag/b9620",
            "assets_url": "https://api.github.com/repos/ggml-org/llama.cpp/releases/789020/assets"
        }
        """;
        var service = CreateService(CreateMockHandler(HttpStatusCode.OK, newerJson), "b9611");
        var result = await service.CheckAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.HasUpdate);
        Assert.Equal("b9620", result.LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_ConsecutiveHexVersions_Correct()
    {
        var service = CreateService(CreateMockHandler(HttpStatusCode.OK, LatestLlamaCppReleaseJson), "b9615");
        var result = await service.CheckAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.HasUpdate);
        Assert.Equal("b9616", result.LatestVersion);
    }

    /// <summary>
    /// Simple mock HttpMessageHandler for testing HTTP scenarios.
    /// </summary>
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string? _content;
        private readonly TimeSpan? _delay;

        public MockHttpMessageHandler(HttpStatusCode statusCode, string? content = null, TimeSpan? delay = null)
        {
            _statusCode = statusCode;
            _content = content;
            _delay = delay;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = new HttpResponseMessage(_statusCode);

            if (_content != null && _statusCode == HttpStatusCode.OK)
            {
                response.Content = new StringContent(_content, Encoding.UTF8, "application/json");
            }

            if (_delay.HasValue)
            {
                return Task.Delay(_delay.Value, cancellationToken).ContinueWith(
                    _ => response,
                    cancellationToken,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }

            return Task.FromResult(response);
        }
    }
}
