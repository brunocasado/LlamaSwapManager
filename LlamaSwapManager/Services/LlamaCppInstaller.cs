using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

internal sealed class LlamaCppInstaller
{
    private readonly string _downloadsDirectory;
    private readonly LlamaCppProcessManager _processManager;
    private readonly LlamaCppPlatformConfigurator _platformConfigurator;
    private readonly Action<string>? _log;

    public LlamaCppInstaller(
        string downloadsDirectory,
        LlamaCppProcessManager processManager,
        LlamaCppPlatformConfigurator platformConfigurator,
        Action<string>? log = null)
    {
        _downloadsDirectory = downloadsDirectory;
        _processManager = processManager;
        _platformConfigurator = platformConfigurator;
        _log = log;
    }

    public async Task<bool> InstallAsync(
        string tempDirectory,
        string archivePath,
        string targetDirectory,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        try
        {
            Directory.CreateDirectory(targetDirectory);
            Directory.CreateDirectory(_downloadsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"[llama.cpp] Cannot prepare installation directories: {ex.Message}");
            return false;
        }

        var backupPath = Path.Combine(
            _downloadsDirectory,
            $"llama-cpp-backup-{Guid.NewGuid():N}");

        _log?.Invoke("[llama.cpp] Stopping llama-server processes before update...");
        await _processManager.StopManagedServerAsync(targetDirectory, ct);
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        try
        {
            _log?.Invoke("[llama.cpp] Backing up existing llama.cpp files...");
            CopyDirectory(targetDirectory, backupPath, ct);

            var stagingDirectory = Path.Combine(tempDirectory, "staging");
            Directory.CreateDirectory(stagingDirectory);

            _log?.Invoke("[llama.cpp] Extracting archive...");
            if (!ExtractArchive(archivePath, stagingDirectory, ct))
            {
                Rollback(backupPath, targetDirectory);
                return false;
            }

            var stagingFiles = Directory
                .EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(stagingDirectory, file))
                .ToList();
            _log?.Invoke(
                $"[llama.cpp] Extracted {stagingFiles.Count} files: {string.Join(", ", stagingFiles)}");

            _log?.Invoke("[llama.cpp] Installing new files...");
            CopyDirectoryContents(stagingDirectory, targetDirectory, ct);
            _platformConfigurator.ConfigureBinaries(targetDirectory, ct);

            var serverBinary = OperatingSystem.IsWindows()
                ? "llama-server.exe"
                : "llama-server";
            var expectedPath = Path.Combine(targetDirectory, serverBinary);
            if (!File.Exists(expectedPath))
            {
                _log?.Invoke(
                    $"[llama.cpp] Installed binary '{serverBinary}' not found at '{expectedPath}' — rollback");
                Rollback(backupPath, targetDirectory);
                return false;
            }

            if (OperatingSystem.IsMacOS())
            {
                await _platformConfigurator.RemoveQuarantineAsync(targetDirectory, ct);
                if (!await _platformConfigurator.VerifyCodesignAsync(expectedPath, ct))
                {
                    _log?.Invoke(
                        "[llama.cpp] Warning: codesign verification failed — binary may not be signed by a known developer");
                }
            }

            DeleteDirectory(backupPath);
            return true;
        }
        catch (OperationCanceledException)
        {
            _log?.Invoke("[llama.cpp] Installation cancelled — rolling back");
            Rollback(backupPath, targetDirectory);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log?.Invoke($"[llama.cpp] Install error: {ex.Message}");
            Rollback(backupPath, targetDirectory);
            return false;
        }
    }

    internal void CopyDirectoryContents(
        string sourceDirectory,
        string targetDirectory,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(targetDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            var destination = Path.Combine(targetDirectory, fileName);
            File.Copy(file, destination, overwrite: true);
        }

        foreach (var subdirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            ct.ThrowIfCancellationRequested();
            CopyDirectoryContents(
                subdirectory,
                Path.Combine(targetDirectory, Path.GetFileName(subdirectory)),
                ct);
        }
    }

    internal static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private bool ExtractArchive(
        string archivePath,
        string stagingDirectory,
        CancellationToken ct)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ArchiveExtractor.ExtractZip(archivePath, stagingDirectory, ct);
            return true;
        }

        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            ArchiveExtractor.ExtractTarGz(archivePath, stagingDirectory, ct);
            return true;
        }

        _log?.Invoke("[llama.cpp] Unsupported archive format");
        return false;
    }

    private static void CopyDirectory(
        string sourceDirectory,
        string targetDirectory,
        CancellationToken ct)
    {
        if (!Directory.Exists(sourceDirectory))
            return;

        Directory.CreateDirectory(targetDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(
                file,
                Path.Combine(targetDirectory, Path.GetFileName(file)),
                overwrite: true);
        }

        foreach (var subdirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            ct.ThrowIfCancellationRequested();
            CopyDirectory(
                subdirectory,
                Path.Combine(targetDirectory, Path.GetFileName(subdirectory)),
                ct);
        }
    }

    private void Rollback(string backupPath, string targetDirectory)
    {
        _log?.Invoke("[llama.cpp] Rolling back to backup...");

        try
        {
            if (Directory.Exists(targetDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(targetDirectory))
                    File.Delete(file);
                foreach (var directory in Directory.EnumerateDirectories(targetDirectory))
                    DeleteDirectory(directory);
            }

            if (!Directory.Exists(backupPath))
            {
                _log?.Invoke("[llama.cpp] Rollback failed — backup not found");
                return;
            }

            CopyDirectoryContents(backupPath, targetDirectory);
            _log?.Invoke("[llama.cpp] Rollback complete");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"[llama.cpp] Rollback error: {ex.Message}");
        }
    }
}
