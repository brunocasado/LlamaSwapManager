using System.Net;
using System.Text;
using LlamaSwapManager.Services;

namespace LlamaSwapManager.Tests;

public sealed class LlamaDeckUpdateServiceTests
{
    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("0.2.0+build.123", "0.2.0")]
    [InlineData("0.2.0-beta.1", "0.2.0")]
    [InlineData("1.4", "1.4.0")]
    [InlineData("invalid", "0.0.0")]
    public void NormalizeVersion_ReturnsStableSemanticVersion(string input, string expected)
    {
        Assert.Equal(expected, LlamaDeckUpdateService.NormalizeVersion(input));
    }

    [Theory]
    [InlineData("0.1.0", "0.2.0", true)]
    [InlineData("v0.2.0", "0.2.0", false)]
    [InlineData("1.0.0", "0.9.0", false)]
    public void IsNewerVersion_ComparesSemanticVersions(
        string current,
        string latest,
        bool expected)
    {
        Assert.Equal(expected, LlamaDeckUpdateService.IsNewerVersion(current, latest));
    }

    [Fact]
    public async Task CheckAsync_SelectsCurrentPlatformAssetFromLatestTag()
    {
        var assetName = LlamaDeckUpdateService.GetExpectedAssetName("0.2.0");
        Assert.False(string.IsNullOrWhiteSpace(assetName));

        var json = $$"""
        {
          "tag_name": "v0.2.0",
          "html_url": "https://github.com/brunocasado/LlamaDeck/releases/tag/v0.2.0",
          "assets": [
            {
              "name": "{{assetName}}",
              "browser_download_url": "https://example.test/{{assetName}}",
              "digest": "sha256:abcdef"
            }
          ]
        }
        """;
        using var http = new HttpClient(new StaticResponseHandler(json));
        using var service = new LlamaDeckUpdateService(
            http,
            currentVersion: "0.1.0",
            processPath: "/tmp/LlamaSwapManager.Desktop");

        var result = await service.CheckAsync();

        Assert.NotNull(result);
        Assert.Equal("0.1.0", result.CurrentVersion);
        Assert.Equal("0.2.0", result.LatestVersion);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(assetName, result.AssetName);
        Assert.Equal($"https://example.test/{assetName}", result.AssetUrl);
        Assert.Equal("sha256:abcdef", result.AssetDigest);
    }

    [Fact]
    public async Task CheckAsync_ReturnsUpdateWithoutAssetForUnsupportedPackage()
    {
        const string json = """
        {
          "tag_name": "v9.0.0",
          "html_url": "https://example.test/release",
          "assets": []
        }
        """;
        using var http = new HttpClient(new StaticResponseHandler(json));
        using var service = new LlamaDeckUpdateService(http, currentVersion: "0.1.0");

        var result = await service.CheckAsync();

        Assert.NotNull(result);
        Assert.True(result.IsUpdateAvailable);
        Assert.NotEmpty(result.AssetName);
        Assert.Empty(result.AssetUrl);
    }

    [Theory]
    [InlineData("/usr/local/share/dotnet/dotnet", false)]
    [InlineData("C:\\Program Files\\dotnet\\dotnet.exe", false)]
    [InlineData("/Applications/LlamaDeck.app/Contents/MacOS/LlamaSwapManager.Desktop", true)]
    [InlineData("C:\\Apps\\LlamaDeck\\LlamaSwapManager.Desktop.exe", true)]
    public void CanInstallUpdate_RequiresPackagedExecutable(string processPath, bool expected)
    {
        using var service = new LlamaDeckUpdateService(
            new HttpClient(new StaticResponseHandler("{}")),
            currentVersion: "0.1.0",
            processPath: processPath);

        Assert.Equal(expected, service.CanInstallUpdate);
    }

    [Fact]
    public void ExtractPackage_PreservesMacAppBundleDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"llamadeck-update-test-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(tempRoot, "source", "LlamaDeck.app", "Contents", "MacOS");
        var archivePath = Path.Combine(tempRoot, "LlamaDeck.zip");
        var stagingDirectory = Path.Combine(tempRoot, "staging");

        try
        {
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(Path.Combine(sourceDirectory, "LlamaSwapManager.Desktop"), "binary");
            System.IO.Compression.ZipFile.CreateFromDirectory(
                Path.Combine(tempRoot, "source"),
                archivePath);

            LlamaDeckUpdateService.ExtractPackage(archivePath, stagingDirectory);

            Assert.True(File.Exists(Path.Combine(
                stagingDirectory,
                "LlamaDeck.app",
                "Contents",
                "MacOS",
                "LlamaSwapManager.Desktop")));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class StaticResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
