using System.Net;
using System.Security.Cryptography;
using System.Text;
using LlamaSwapManager.Services;

namespace LlamaSwapManager.Tests;

public sealed class LlamaCppServicesTests
{
    [Theory]
    [InlineData("version: 9553 (9e3b928fd)", "b9553")]
    [InlineData("build (9e3b928fd)", "b9e3b9")]
    [InlineData("unrecognized output", null)]
    public void VersionDetector_ParsesSupportedFormats(string output, string? expected)
    {
        Assert.Equal(expected, LlamaCppVersionDetector.ParseVersion(output));
    }

    [Fact]
    public void ProcessManager_MatchesOnlyExactManagedPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"process-path-{Guid.NewGuid():N}");
        var expected = Path.Combine(root, "llama-server");

        Assert.True(LlamaCppProcessManager.IsManagedProcessPath(
            expected,
            expected,
            StringComparison.Ordinal));
        Assert.False(LlamaCppProcessManager.IsManagedProcessPath(
            Path.Combine(root, "other", "llama-server"),
            expected,
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task ArtifactDownloader_VerifiesValidChecksum()
    {
        var path = Path.Combine(Path.GetTempPath(), $"checksum-{Guid.NewGuid():N}.bin");
        try
        {
            var content = Encoding.UTF8.GetBytes("llama.cpp");
            await File.WriteAllBytesAsync(path, content);
            var digest = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            using var http = new HttpClient();
            var downloader = new LlamaCppArtifactDownloader(http);

            Assert.True(await downloader.VerifyChecksumAsync(path, $"sha256:{digest}", CancellationToken.None));
            Assert.False(await downloader.VerifyChecksumAsync(path, $"sha256:{new string('0', 64)}", CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Installer_CopyDirectoryContents_CopiesRecursively()
    {
        using var fixture = new DirectoryFixture();
        var source = Path.Combine(fixture.Root, "source");
        var destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllText(Path.Combine(source, "llama-server"), "server");
        File.WriteAllText(Path.Combine(source, "nested", "library.bin"), "library");

        var installer = new LlamaCppInstaller(
            Path.Combine(fixture.Root, "downloads"),
            new LlamaCppProcessManager(),
            new LlamaCppPlatformConfigurator());

        installer.CopyDirectoryContents(source, destination, CancellationToken.None);

        Assert.Equal("server", File.ReadAllText(Path.Combine(destination, "llama-server")));
        Assert.Equal("library", File.ReadAllText(Path.Combine(destination, "nested", "library.bin")));
    }

    [Fact]
    public void Installer_CopyDirectoryContents_RespectsCancellation()
    {
        using var fixture = new DirectoryFixture();
        var source = Path.Combine(fixture.Root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "file.bin"), "data");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var installer = new LlamaCppInstaller(
            Path.Combine(fixture.Root, "downloads"),
            new LlamaCppProcessManager(),
            new LlamaCppPlatformConfigurator());

        Assert.Throws<OperationCanceledException>(() =>
            installer.CopyDirectoryContents(
                source,
                Path.Combine(fixture.Root, "destination"),
                cancellation.Token));
    }

    [Fact]
    public void AssetSelector_PrefersExactCudaVersion()
    {
        var selector = new LlamaCppAssetSelector();
        var assets = new List<LlamaCppAssetSelector.CudaAsset>
        {
            new("cuda-12.4", "url", 1, "digest", LlamaCppAssetSelector.CudaAssetType.LlamaBuild, "12.4"),
            new("cuda-12.6", "url", 1, "digest", LlamaCppAssetSelector.CudaAssetType.LlamaBuild, "12.6")
        };

        var selected = selector.FindBestCudaAsset(assets, "12.4");

        Assert.NotNull(selected);
        Assert.Equal("12.4", selected.CudaVersion);
    }

    [Fact]
    public void AssetSelector_UsesNewestMatchingMajorVersion()
    {
        var selector = new LlamaCppAssetSelector();
        var assets = new List<LlamaCppAssetSelector.CudaAsset>
        {
            new("cuda-12.4", "url", 1, "digest", LlamaCppAssetSelector.CudaAssetType.LlamaBuild, "12.4"),
            new("cuda-12.6", "url", 1, "digest", LlamaCppAssetSelector.CudaAssetType.LlamaBuild, "12.6"),
            new("cuda-11.8", "url", 1, "digest", LlamaCppAssetSelector.CudaAssetType.LlamaBuild, "11.8")
        };

        var selected = selector.FindBestCudaAsset(assets, "12.5");

        Assert.NotNull(selected);
        Assert.Equal("12.6", selected.CudaVersion);
    }

    [Fact]
    public async Task GitHubReleaseClient_ReturnsDetachedJsonElement()
    {
        using var http = new HttpClient(new StubHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"tag_name\":\"b9999\",\"assets\":[]}")
            })));
        using var fixture = new DirectoryFixture();
        var client = new GitHubReleaseClient(http, "ggml-org/llama.cpp", fixture.Root);

        var release = await client.GetLatestReleaseAsync(CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal("b9999", release.Value.GetProperty("tag_name").GetString());
    }

    [Fact]
    public async Task GitHubReleaseClient_PropagatesCallerCancellation()
    {
        using var http = new HttpClient(new StubHttpHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var fixture = new DirectoryFixture();
        var client = new GitHubReleaseClient(http, "ggml-org/llama.cpp", fixture.Root);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.GetLatestReleaseAsync(cancellation.Token));
    }

    [Fact]
    public async Task GitHubReleaseClient_UsesFreshCacheWithoutNetworkCall()
    {
        using var fixture = new DirectoryFixture();
        var calls = 0;
        using var http = new HttpClient(new StubHttpHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"release-1\"") },
                Content = new StringContent("{\"tag_name\":\"b1000\",\"assets\":[]}")
            });
        }));
        var client = new GitHubReleaseClient(http, "ggml-org/llama.cpp", fixture.Root, cacheLifetime: TimeSpan.FromHours(1));

        var first = await client.GetLatestReleaseAsync(CancellationToken.None);
        var second = await client.GetLatestReleaseAsync(CancellationToken.None);

        Assert.Equal("b1000", first?.GetProperty("tag_name").GetString());
        Assert.Equal("b1000", second?.GetProperty("tag_name").GetString());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GitHubReleaseClient_UsesEtagAndHandlesNotModified()
    {
        using var fixture = new DirectoryFixture();
        var calls = 0;
        using var http = new HttpClient(new StubHttpHandler((request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"release-1\"") },
                    Content = new StringContent("{\"tag_name\":\"b1000\",\"assets\":[]}")
                });
            }

            Assert.Contains(request.Headers.IfNoneMatch, tag => tag.Tag == "\"release-1\"");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        }));
        var client = new GitHubReleaseClient(http, "ggml-org/llama.cpp", fixture.Root, cacheLifetime: TimeSpan.Zero);

        await client.GetLatestReleaseAsync(CancellationToken.None);
        var release = await client.GetLatestReleaseAsync(CancellationToken.None);

        Assert.Equal("b1000", release?.GetProperty("tag_name").GetString());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GitHubReleaseClient_FallsBackToCacheOnRateLimit()
    {
        using var fixture = new DirectoryFixture();
        var calls = 0;
        using var http = new HttpClient(new StubHttpHandler((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"tag_name\":\"b1000\",\"assets\":[]}")
                });
            }

            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return Task.FromResult(response);
        }));
        var client = new GitHubReleaseClient(http, "ggml-org/llama.cpp", fixture.Root, cacheLifetime: TimeSpan.Zero);

        await client.GetLatestReleaseAsync(CancellationToken.None);
        var rateLimited = await client.GetLatestReleaseAsync(CancellationToken.None);
        var deferred = await client.GetLatestReleaseAsync(CancellationToken.None);

        Assert.Equal("b1000", rateLimited?.GetProperty("tag_name").GetString());
        Assert.Equal("b1000", deferred?.GetProperty("tag_name").GetString());
        Assert.Equal(2, calls);
    }

    [Fact]
    public void PlatformConfigurator_SelectsNewestDylibPerLibrary()
    {
        using var fixture = new DirectoryFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "libllama.0.0.100.dylib"), "old");
        File.WriteAllText(Path.Combine(fixture.Root, "libllama.0.0.200.dylib"), "new");
        File.WriteAllText(Path.Combine(fixture.Root, "libggml.0.1.50.dylib"), "ggml");

        var groups = LlamaCppPlatformConfigurator.FindNewestDylibs(fixture.Root);

        Assert.Equal(2, groups.Count);
        Assert.EndsWith("libllama.0.0.200.dylib", groups["libllama.0"].Path);
        Assert.EndsWith("libggml.0.1.50.dylib", groups["libggml.0"].Path);
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHttpHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }

    private sealed class DirectoryFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(),
            $"llama-services-{Guid.NewGuid():N}");

        public DirectoryFixture() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { }
        }
    }
}
