using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace LlamaSwapManager.Services;

/// <summary>
/// Extracts release archives into a staging directory using the platform
/// implementations provided by .NET. A single wrapper directory is removed
/// so callers receive the same flat layout for zip and tar.gz releases.
/// </summary>
public static class ArchiveExtractor
{
    public static void ExtractZip(string archivePath, string destinationDir, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        PrepareDestination(destinationDir);
        ct.ThrowIfCancellationRequested();

        ZipFile.ExtractToDirectory(archivePath, destinationDir, overwriteFiles: true);
        ct.ThrowIfCancellationRequested();
        FlattenSingleRootDirectory(destinationDir);
    }

    public static void ExtractTarGz(string archivePath, string destinationDir, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        PrepareDestination(destinationDir);
        ct.ThrowIfCancellationRequested();

        using var archive = File.OpenRead(archivePath);
        using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destinationDir, overwriteFiles: true);

        ct.ThrowIfCancellationRequested();
        FlattenSingleRootDirectory(destinationDir);
    }

    private static void PrepareDestination(string destinationDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDir);
        Directory.CreateDirectory(destinationDir);
    }

    private static void FlattenSingleRootDirectory(string destinationDir)
    {
        if (Directory.EnumerateFiles(destinationDir).Any())
            return;

        var directories = Directory.EnumerateDirectories(destinationDir).Take(2).ToArray();
        if (directories.Length != 1)
            return;

        var root = directories[0];
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var destination = Path.Combine(destinationDir, Path.GetFileName(entry));
            if (File.Exists(entry))
                File.Move(entry, destination, overwrite: true);
            else
                Directory.Move(entry, destination);
        }

        Directory.Delete(root);
    }
}
