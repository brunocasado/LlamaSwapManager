using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace LlamaSwapManager.Services;

/// <summary>
/// Detects the installed NVIDIA CUDA toolkit version on the current platform.
/// Returns null if CUDA is not installed or on macOS.
/// </summary>
public static class CudaVersionDetector
{
    private static string? _cachedVersion;
    private static bool _cachePopulated;

    /// <summary>
    /// Regex to parse CUDA version from nvcc output, e.g. "release 12.6, V12.6.85-33333333".
    /// Captures major.minor as group 1 and full version as group 2.
    /// </summary>
    private static readonly Regex NvccVersionRegex = new(
        @"release\s+(\d+\.\d+),\s*V?(\d+\.\d+\.\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// Regex to parse CUDA version from cuda_version.txt, e.g. "12060".
    /// Captures the numeric string.
    /// </summary>
    private static readonly Regex CudaVersionFileRegex = new(
        @"(\d{5,})",
        RegexOptions.Compiled);

    /// <summary>
    /// Regex to parse CUDA version from a directory name like "cuda-12.6" or "cuda12.6".
    /// </summary>
    private static readonly Regex CudaDirRegex = new(
        @"cuda[\/\-_]?(\d+\.\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Regex to parse CUDA version from Windows Registry value.
    /// </summary>
    private static readonly Regex RegistryVersionRegex = new(
        @"(\d+\.\d+\.\d+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Detect the installed CUDA toolkit version.
    /// Returns a string like "12.6" on success, null if CUDA is not found.
    /// Result is cached per session.
    /// </summary>
    public static string? GetCudaVersion()
    {
        if (_cachePopulated)
            return _cachedVersion;

        _cachePopulated = true;

        // macOS — CUDA not applicable
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            _cachedVersion = null;
            return null;
        }

        // Try detection methods in priority order
        _cachedVersion = TryNvcc()
            ?? TryCudaVersionFile()
            ?? TryDirectoryScan()
            ?? TryRegistry();

        return _cachedVersion;
    }

    /// <summary>
    /// Clear the cached CUDA version (useful for re-detection after install).
    /// </summary>
    public static void ClearCache()
    {
        _cachePopulated = false;
        _cachedVersion = null;
    }

    // ---- Detection methods ----

    /// <summary>
    /// Method 1: Run nvcc --version and parse the output.
    /// </summary>
    private static string? TryNvcc()
    {
        try
        {
            var output = RunCommand("nvcc", "--version", 5000);
            if (string.IsNullOrEmpty(output))
                return null;

            var match = NvccVersionRegex.Match(output);
            if (match.Success)
            {
                // Return major.minor (e.g. "12.6")
                return match.Groups[1].Value;
            }

            // Fallback: try to extract version from "V12.6.85" pattern
            var fallback = Regex.Match(output, @"V(\d+\.\d+\.\d+)");
            if (fallback.Success)
            {
                var parts = fallback.Groups[1].Value.Split('.');
                return $"{parts[0]}.{parts[1]}";
            }
        }
        catch
        {
            // nvcc not found or crashed — try next method
        }

        return null;
    }

    /// <summary>
    /// Method 2 (Linux): Read /usr/local/cuda/version.txt or similar.
    /// </summary>
    private static string? TryCudaVersionFile()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return null;

        var candidatePaths = new[]
        {
            "/usr/local/cuda/version.txt",
            "/usr/local/cuda-/version.txt",
            "/opt/cuda/version.txt",
        };

        // For the wildcard path, scan actual cuda-* dirs
        var cudaDirs = Directory.Exists("/usr/local")
            ? Directory.GetDirectories("/usr/local", "cuda-*")
                .Where(d => CudaDirRegex.IsMatch(Path.GetFileName(d)))
                .Select(d => Path.Combine(d, "version.txt"))
                .ToArray()
            : Array.Empty<string>();

        var allPaths = candidatePaths
            .Where(p => !p.Contains("*"))
            .Concat(cudaDirs)
            .Distinct()
            .ToArray();

        foreach (var path in allPaths)
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                var content = File.ReadAllText(path).Trim();
                var match = CudaVersionFileRegex.Match(content);
                if (match.Success)
                {
                    // Convert "12060" -> "12.6"
                    var numStr = match.Groups[1].Value;
                    if (numStr.Length >= 4)
                    {
                        return $"{numStr[0]}.{numStr[1]}";
                    }
                }
            }
            catch
            {
                // Skip unreadable files
            }
        }

        return null;
    }

    /// <summary>
    /// Method 3: Scan common CUDA installation directories for version info.
    /// </summary>
    private static string? TryDirectoryScan()
    {
        var candidateDirs = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrEmpty(programFiles))
            {
                var cudaPath = Path.Combine(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA");
                if (Directory.Exists(cudaPath))
                    candidateDirs.AddRange(Directory.GetDirectories(cudaPath));
            }

            // Also check C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\vX.Y
            var cudaVPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA GPU Computing Toolkit", "CUDA");
            if (Directory.Exists(cudaVPath))
                candidateDirs.AddRange(Directory.GetDirectories(cudaVPath));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var commonPaths = new[]
            {
                "/usr/local",
                "/opt",
            };

            foreach (var baseDir in commonPaths)
            {
                if (!Directory.Exists(baseDir))
                    continue;

                foreach (var dir in Directory.GetDirectories(baseDir))
                {
                    var name = Path.GetFileName(dir);
                    if (CudaDirRegex.IsMatch(name))
                        candidateDirs.Add(dir);
                }
            }
        }

        // Try to extract version from directory names
        foreach (var dir in candidateDirs)
        {
            var dirName = Path.GetFileName(dir);
            var match = CudaDirRegex.Match(dirName);
            if (match.Success)
                return match.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// Method 4 (Windows): Read CUDA version from Registry.
    /// </summary>
    private static string? TryRegistry()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        try
        {
            // Enumerate CUDA version subkeys under the toolkit path
            var toolkitKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\NVIDIA Corporation\NVIDIA GPU Computing Toolkit\CUDA");

            if (toolkitKey != null)
            {
                foreach (var subKeyName in toolkitKey.GetSubKeyNames())
                {
                    if (!CudaDirRegex.IsMatch(subKeyName))
                        continue;

                    using var versionKey = toolkitKey.OpenSubKey(subKeyName);
                    if (versionKey == null) continue;

                    var version = versionKey.GetValue("Version")?.ToString();
                    if (!string.IsNullOrEmpty(version))
                    {
                        var match = RegistryVersionRegex.Match(version);
                        if (match.Success)
                        {
                            var parts = match.Groups[1].Value.Split('.');
                            return $"{parts[0]}.{parts[1]}";
                        }
                    }
                }
            }

            // Also check the simpler registry path
            using var key2 = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\NVIDIA Corporation\Global\CUDA\Products");

            if (key2 != null)
            {
                var names = key2.GetValue("Name") as string[];
                if (names != null && names.Length > 0)
                {
                    // First CUDA product found
                    var name = names[0];
                    var verMatch = Regex.Match(name, @"CUDA\s+(\d+\.\d+)");
                    if (verMatch.Success)
                        return verMatch.Groups[1].Value;
                }
            }
        }
        catch
        {
            // Registry access denied or key not found
        }

        return null;
    }

    /// <summary>
    /// Run a command and return stdout (trimmed). Returns empty string on failure.
    /// </summary>
    private static string RunCommand(string command, string args, int timeoutMs)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return "";

            proc.WaitForExit(timeoutMs);
            var output = proc.StandardOutput.ReadToEnd();
            if (proc.ExitCode != 0)
            {
                output += proc.StandardError.ReadToEnd();
            }

            return output.Trim();
        }
        catch
        {
            return "";
        }
    }
}
