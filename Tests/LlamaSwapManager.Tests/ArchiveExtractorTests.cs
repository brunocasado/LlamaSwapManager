using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using LlamaSwapManager.Services;

namespace LlamaSwapManager.Tests;

public sealed class ArchiveExtractorTests
{
    [Fact]
    public void ExtractZip_FlattensSingleRootDirectory()
    {
        using var fixture = new ArchiveFixture();
        var archivePath = Path.Combine(fixture.Root, "release.zip");
        var destination = Path.Combine(fixture.Root, "zip-output");

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("release/llama-server");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("binary");
        }

        ArchiveExtractor.ExtractZip(archivePath, destination);

        Assert.Equal("binary", File.ReadAllText(Path.Combine(destination, "llama-server")));
        Assert.False(Directory.Exists(Path.Combine(destination, "release")));
    }

    [Fact]
    public void ExtractTarGz_FlattensSingleRootDirectory()
    {
        using var fixture = new ArchiveFixture();
        var archivePath = Path.Combine(fixture.Root, "release.tar.gz");
        var destination = Path.Combine(fixture.Root, "tar-output");

        using (var file = File.Create(archivePath))
        using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
        using (var writer = new TarWriter(gzip))
        {
            var data = new MemoryStream(Encoding.UTF8.GetBytes("binary"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "release/llama-server")
            {
                DataStream = data
            });
        }

        ArchiveExtractor.ExtractTarGz(archivePath, destination);

        Assert.Equal("binary", File.ReadAllText(Path.Combine(destination, "llama-server")));
        Assert.False(Directory.Exists(Path.Combine(destination, "release")));
    }

    [Fact]
    public void ExtractZip_PreservesFlatArchiveLayout()
    {
        using var fixture = new ArchiveFixture();
        var archivePath = Path.Combine(fixture.Root, "flat.zip");
        var destination = Path.Combine(fixture.Root, "flat-output");

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using (var first = new StreamWriter(archive.CreateEntry("llama-server").Open()))
                first.Write("server");

            using (var second = new StreamWriter(archive.CreateEntry("README.md").Open()))
                second.Write("readme");
        }

        ArchiveExtractor.ExtractZip(archivePath, destination);

        Assert.Equal("server", File.ReadAllText(Path.Combine(destination, "llama-server")));
        Assert.Equal("readme", File.ReadAllText(Path.Combine(destination, "README.md")));
    }

    private sealed class ArchiveFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"archive-tests-{Guid.NewGuid()}");

        public ArchiveFixture() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { }
        }
    }
}
