using LlamaSwapManager.Models;
using LlamaSwapManager.Services;
using System.Net;
using System.Text;

namespace LlamaSwapManager.Tests;

public class VersionCheckServiceTests
{
    private static readonly string LatestReleaseJson = """
    {
        "tag_name": "v224",
        "html_url": "https://github.com/mostlygeek/llama-swap/releases/tag/v224",
        "assets_url": "https://api.github.com/repos/mostlygeek/llama-swap/releases/123456/assets"
    }
    """;

    private static readonly string OldReleaseJson = """
    {
        "tag_name": "v220",
        "html_url": "https://github.com/mostlygeek/llama-swap/releases/tag/v220",
        "assets_url": "https://api.github.com/repos/mostlygeek/llama-swap/releases/123450/assets"
    }
    """;

    private static readonly string LlamaCppReleaseJson = """
    {
        "tag_name": "b9616",
        "html_url": "https://github.com/mostlygeek/llama-swap/releases/tag/b9616",
        "assets_url": "https://api.github.com/repos/mostlygeek/llama-swap/releases/123457/assets"
    }
    """;

   private static HttpMessageHandler CreateMockHandler(HttpStatusCode statusCode, string? content = null, TimeSpan? delay = null)
    {
        return new MockHttpMessageHandler(statusCode, content, delay);
    }

    private static VersionCheckService CreateService(
        HttpMessageHandler handler,
        string currentVersion = "v223",
        string owner = "mostlygeek",
        string repo = "llama-swap")
    {
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        return new VersionCheckService(httpClient, owner, repo, currentVersion);
    }

    // --- Happy path: update available ---

    [Fact]
    public async Task CheckAsync_NewerVersionAvailable_ReturnsHasUpdateTrue()
    {
        var service = CreateService(CreateMockHandler(HttpStatusCode.OK, LatestReleaseJson), "v223");
        var result = await service.CheckAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.HasUpdate);
        Assert.Equal("v223", result.CurrentVersion);
        Assert.Equal("v224", result.LatestVersion);
        Assert.Equal("v224", result.ReleaseTag);
        Assert.Equal("https://github.com/mostlygeek/llama-swap/releases/tag/v224", result.DownloadUrl);
        Assert.NotNull(result.AssetsUrl);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CheckAsync_UpToDate_ReturnsHasUpdateFalse()
    {
        var service = CreateService(CreateMockHandler(HttpStatusCode.OK, LatestReleaseJson), "v224");
        var result = await service.CheckAsync();

        Assert.True(result.IsSuccess);
        Assert.False(result.HasUpdate);
        Assert.Equal("v224", result.LatestVersion);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CheckAsync_OlderVersion_ReturnsHasUpdateFalse()
    {
        var service = CreateService(CreateMockHandler(HttpStatusCode.OK, OldReleaseJson), "v224");
        var result = await service.CheckAsync();

        Assert.True(result.IsSuccess);
        Assert.False(result.HasUpdate);
        Assert.Equal("v220", result.LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_LlamaCppVersion_ReturnsCorrectType()
    {
        var service = CreateService(CreateMockHandler(HttpStatusCode.OK, LlamaCppReleaseJson), "b9610");
        var result = await service.CheckAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.HasUpdate);
        Assert.Equal("b9616", result.LatestVersion);
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
            "html_url": "https://github.com/mostlygeek/llama-swap/releases/tag/v224",
            "assets_url": "https://api.github.com/repos/mostlygeek/llama-swap/releases/123456/assets"
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
            "html_url": "https://github.com/mostlygeek/llama-swap/releases/tag/",
            "assets_url": "https://api.github.com/repos/mostlygeek/llama-swap/releases/123456/assets"
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
        // Use a delay exceeding the HttpClient's 10s timeout
        var service = CreateService(CreateMockHandler(HttpStatusCode.OK, LatestReleaseJson, TimeSpan.FromSeconds(30)), "v223");
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
        var service = CreateService(CreateMockHandler(HttpStatusCode.OK, LatestReleaseJson), "v223");
        var result = await service.CheckAsync(cts.Token);

        // Cancellation is handled gracefully: success with no update
        Assert.True(result.IsSuccess);
        Assert.False(result.HasUpdate);
    }

    // --- Synchronous Check method ---

    [Fact]
    public void Check_Synchronous_ReturnsResult()
    {
        var service = CreateService(CreateMockHandler(HttpStatusCode.OK, LatestReleaseJson), "v223");
        var result = service.Check();

        Assert.True(result.HasUpdate);
        Assert.Equal("v224", result.LatestVersion);
    }

    // --- Dependency injection friendly ---

    [Fact]
    public void Constructor_SetsDefaults()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, LatestReleaseJson);
        var httpClient = new HttpClient(handler);
        var service = new VersionCheckService(httpClient, "owner", "repo", "v1");

        // Verify it works with default timeout
        var result = service.Check();
        Assert.True(result.HasUpdate);
    }

    // --- VersionCheckResult model ---

    [Fact]
    public void VersionCheckResult_DefaultsAreCorrect()
    {
        var result = new VersionCheckResult();
        Assert.False(result.HasUpdate);
        Assert.Equal("", result.CurrentVersion);
        Assert.Equal("", result.LatestVersion);
        Assert.Null(result.DownloadUrl);
        Assert.Null(result.AssetsUrl);
        Assert.Null(result.ReleaseTag);
        Assert.Null(result.Error);
        Assert.True(result.IsSuccess); // null error = success
    }

    [Fact]
    public void VersionCheckResult_IsSuccess_FalseOnError()
    {
        var result = new VersionCheckResult { Error = "something broke" };
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void VersionCheckResult_IsSuccess_TrueWhenNoError()
    {
        var result = new VersionCheckResult { HasUpdate = true, LatestVersion = "v224" };
        Assert.True(result.IsSuccess);
    }

    // --- Edge cases ---

    [Fact]
    public async Task CheckAsync_EmptyOwnerOrRepo_ReturnsError()
    {
        var service = CreateService(CreateMockHandler(HttpStatusCode.NotFound), currentVersion: "v1", owner: "", repo: "");
        var result = await service.CheckAsync();

        // Should fail gracefully, not throw
        Assert.NotNull(result);
        Assert.False(result.HasUpdate);
    }

    [Fact]
    public async Task CheckAsync_LlamaSwapToLlamaCpp_ReturnsNoUpdate()
    {
        // Different version types should not trigger update
        var service = CreateService(CreateMockHandler(HttpStatusCode.OK, LlamaCppReleaseJson), "v223");
        var result = await service.CheckAsync();

        Assert.True(result.IsSuccess);
        Assert.False(result.HasUpdate); // v223 vs b9616 = different types = not comparable
    }

    [Fact]
    public async Task CheckAsync_LlamaCppToLlamaSwap_ReturnsNoUpdate()
    {
        var service = CreateService(CreateMockHandler(HttpStatusCode.OK, LatestReleaseJson), "b9610");
        var result = await service.CheckAsync();

        Assert.True(result.IsSuccess);
        Assert.False(result.HasUpdate); // b9610 vs v224 = different types = not comparable
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
