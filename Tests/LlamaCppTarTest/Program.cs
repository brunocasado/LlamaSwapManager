using System;
using System.IO;
using System.Linq;
using LlamaSwapManager.Services;

class Program
{
    static void Main()
    {
        var archivePath = "/tmp/llama-cpp-test.tar.gz";
        var extractDir = "/tmp/llamacpp-cs-test";
        Directory.CreateDirectory(extractDir);

        using var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gzip = new System.IO.Compression.GZipStream(archive, System.IO.Compression.CompressionMode.Decompress);

        var tarArchive = new TarArchive(gzip);
        var entries = tarArchive.Entries;

        Console.WriteLine($"Total entries: {entries.Count}");
        Console.WriteLine("First 5 entries:");
        foreach (var e in entries.Take(5))
        {
            Console.WriteLine($"  {e.Name} (dir={e.IsDirectory}, size={e.DataStream?.Length ?? 0})");
        }

        // Find llama-server
        var serverEntry = entries.FirstOrDefault(e => e.Name.Contains("llama-server"));
        if (serverEntry != null)
        {
            Console.WriteLine($"\nFound llama-server: {serverEntry.Name}");
            Console.WriteLine($"  DataStream length: {serverEntry.DataStream?.Length ?? 0}");

            // Extract to test dir
            var targetPath = Path.Combine(extractDir, Path.GetFileName(serverEntry.Name));
            using var fs = new FileStream(targetPath, FileMode.Create);
            serverEntry.DataStream!.CopyTo(fs);
            Console.WriteLine($"  Extracted to: {targetPath}");
            Console.WriteLine($"  File size: {new FileInfo(targetPath).Length} bytes");
        }
        else
        {
            Console.WriteLine("\nllama-server NOT FOUND in entries!");
        }
    }
}