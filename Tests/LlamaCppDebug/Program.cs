using System;
using System.Diagnostics;
using System.IO;

// Simulate exactly what DetectLocalVersion does
var tempInstallDir = Path.Combine(Path.GetTempPath(), $"debug-llama-{Guid.NewGuid()}");
Directory.CreateDirectory(tempInstallDir);

// Copy the real binary and its dylibs
var srcDir = "/Users/brunocasado/.llama";
foreach (var f in Directory.GetFiles(srcDir, "*.dylib"))
    File.Copy(f, Path.Combine(tempInstallDir, Path.GetFileName(f)), true);
File.Copy(Path.Combine(srcDir, "llama-server"), Path.Combine(tempInstallDir, "llama-server"), true);

Console.WriteLine($"Test dir: {tempInstallDir}");
Console.WriteLine($"Binary exists: {File.Exists(Path.Combine(tempInstallDir, "llama-server"))}");

// Check symlinks
Console.WriteLine("\nDylibs in test dir:");
foreach (var f in Directory.GetFiles(tempInstallDir, "*.dylib").OrderBy(x => x))
    Console.WriteLine($"  {Path.GetFileName(f)}");

// Try to run
try
{
    var psi = new ProcessStartInfo
    {
        FileName = Path.Combine(tempInstallDir, "llama-server"),
        Arguments = "--version",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    
    Console.WriteLine("\nStarting process...");
    var proc = Process.Start(psi);
    Console.WriteLine($"Process started: {proc?.HasExited}");
    
    var stderr = proc.StandardError.ReadToEnd();
    var stdout = proc.StandardOutput.ReadToEnd();
    var exited = proc.WaitForExit(5000);
    var exitCode = proc.ExitCode;
    
    Console.WriteLine($"Exited: {exited}, Code: {exitCode}");
    Console.WriteLine($"STDERR: '{stderr}'");
    Console.WriteLine($"STDOUT: '{stdout}'");
    Console.WriteLine($"Combined: '{stderr + stdout}'");
}
catch (Exception ex)
{
    Console.WriteLine($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
}

// Clean up
Directory.Delete(tempInstallDir, true);
