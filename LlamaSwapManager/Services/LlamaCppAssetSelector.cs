using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlamaSwapManager.Services;

internal sealed class LlamaCppAssetSelector
{
    private readonly Action<string>? _log;

    public LlamaCppAssetSelector(Action<string>? log = null)
    {
        _log = log;
    }

    // ---- Asset Detection ----

    public DetectedAsset? DetectAssetForPlatform(
        string tag, JsonElement? release, string? preferredCudaVersion = null)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        var isMacArm = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
                       RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        var isMacIntel = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
                         RuntimeInformation.ProcessArchitecture == Architecture.X64;

        var platformName = isWindows ? "Windows" : isLinux ? "Linux" : "macOS";
        _log?.Invoke($"[llama.cpp] Platform: {platformName}, Arch: {RuntimeInformation.ProcessArchitecture}, Tag: {tag}");

        var patterns = new List<string?>();

        if (isMacArm)
        {
            // Asset names: llama-b9704-bin-macos-arm64.tar.gz (no trailing dash after arm64)
            patterns.Add("-macos-arm64");
        }
        else if (isMacIntel)
        {
            // Asset names: llama-b9704-bin-macos-x64.tar.gz (no trailing dash after x64)
            patterns.Add("-macos-x64");
        }
        else if (isWindows || isLinux)
        {
            // Windows/Linux: use user preference or auto-detect
            var effectiveBackend = GpuDetectionSettings.GetEffectiveBackend();
            _log?.Invoke($"[llama.cpp] GPU backend setting: {effectiveBackend}");

            var detectedBackends = GpuDetectionService.DetectBackends();
            var detectedNames = string.Join(", ", detectedBackends.Select(d => $"{d.Backend}({d.Priority})"));
            _log?.Invoke($"[llama.cpp] Detected backends: {detectedNames}");

            var userPattern = GpuDetectionService.GetPreferredAssetPattern(effectiveBackend);
            if (userPattern != null)
            {
                patterns.Add(userPattern);
                _log?.Invoke($"[llama.cpp] User preference pattern: {userPattern}");
            }

            // Fall back to auto-detected backends in priority order
            foreach (var gpu in detectedBackends)
            {
                if (gpu.Backend == effectiveBackend) continue;
                var pattern = GpuDetectionService.GetPreferredAssetPattern(gpu.Backend);
                if (pattern != null && !patterns.Contains(pattern))
                {
                    patterns.Add(pattern);
                    _log?.Invoke($"[llama.cpp] Auto-detected pattern: {pattern}");
                }
            }
        }

        if (release == null)
        {
            _log?.Invoke("[llama.cpp] No release info found");
            return null;
        }

        if (!release.Value.TryGetProperty("assets", out var assets))
        {
            _log?.Invoke("[llama.cpp] No assets property found in release");
            return null;
        }

        var assetArray = assets.EnumerateArray().ToList();
        if (!assetArray.Any())
        {
            _log?.Invoke("[llama.cpp] No assets found in release");
            return null;
        }

        // Parse CUDA assets for version-aware selection
        var allCudaAssets = ParseCudaAssets(release.Value);
        var cudartAssets = allCudaAssets.Where(a => a.AssetType == CudaAssetType.Cudart).ToList();
        var llamaCudaAssets = allCudaAssets.Where(a => a.AssetType == CudaAssetType.LlamaBuild).ToList();

        if (llamaCudaAssets.Any())
            _log?.Invoke($"[llama.cpp] Found {llamaCudaAssets.Count} CUDA llama.cpp asset(s): {string.Join(", ", llamaCudaAssets.Select(a => a.Name))}");

        // For CUDA backend on Windows/Linux, use version-aware asset selection
        if (isWindows || isLinux)
        {
            var effectiveBackend = GpuDetectionSettings.GetEffectiveBackend();

            if (effectiveBackend == GpuDetectionService.GpuBackend.Cuda)
            {
                // User-selected CUDA version (from UI picker) or empty string
                // No auto-detection of system CUDA toolkit — llama.cpp bundles its own DLLs
                var userVersion = string.IsNullOrWhiteSpace(preferredCudaVersion)
                    ? null
                    : preferredCudaVersion;

                _log?.Invoke($"[llama.cpp] CUDA version selected: {(userVersion ?? "auto (latest)")}");

                if (userVersion != null)
                {
                    // User explicitly chose a version — find matching CUDA build
                    var bestAsset = FindBestCudaAsset(llamaCudaAssets, userVersion);
                    if (bestAsset != null)
                    {
                        _log?.Invoke($"[llama.cpp] Selected CUDA {userVersion} build: {bestAsset.Name}");
                        return new DetectedAsset(
                            bestAsset.Name,
                            bestAsset.Size,
                            bestAsset.Url,
                            bestAsset.Digest,
                            cudartAssets.Where(a => a.Name.Contains(userVersion)).ToList());
                    }

                    _log?.Invoke($"[llama.cpp] No CUDA {userVersion} asset found, falling back to latest");
                }

                // Fallback: pick latest CUDA build (user didn't specify version)
                var latestAsset = llamaCudaAssets.OrderByDescending(a => a.Name).FirstOrDefault();
                if (latestAsset != null)
                {
                    _log?.Invoke($"[llama.cpp] Selected latest CUDA build: {latestAsset.Name}");
                    return new DetectedAsset(
                        latestAsset.Name,
                        latestAsset.Size,
                        latestAsset.Url,
                        latestAsset.Digest,
                        cudartAssets);
                }

                _log?.Invoke($"[llama.cpp] No CUDA assets available in release, falling back to non-CUDA");
            }
        }

        // Non-CUDA path: use existing pattern-based detection
        var arch = RuntimeInformation.ProcessArchitecture;
        var archFilter = arch == Architecture.Arm64 ? "arm64" : "x64";
        var archiveFormat = isWindows ? ".zip" : ".tar.gz";

         // Filter patterns: if no GPU was detected (only CpuOnly), only use CPU fallback patterns.
            // This prevents selecting CUDA/Vulkan assets when user has GPU preference but no hardware.
            var nonCudaBackends = GpuDetectionService.DetectBackends();
            var hasGpu = nonCudaBackends.Any(g => g.Backend != GpuDetectionService.GpuBackend.CpuOnly);
            _log?.Invoke($"[llama.cpp] Has GPU: {hasGpu}, Patterns: [{string.Join(", ", patterns.Where(p => p != null))}], Format: {archiveFormat}");

        var filteredPatterns = patterns.Where(p =>
        {
            if (p == null) return false;
            if (hasGpu) return true;
            // No GPU detected — only allow CPU fallback patterns
            return p.Contains("-cpu-");
        }).ToList();

        _log?.Invoke($"[llama.cpp] Filtered patterns (GPU filter applied): [{string.Join(", ", filteredPatterns)}]");

        // Try each pattern in priority order
        foreach (var pattern in filteredPatterns)
        {
            if (pattern == null) continue;

            var matches = assetArray
                .Select(a => a.GetProperty("name").GetString() ?? "")
                .Where(name => name.Contains(pattern) && name.EndsWith(archiveFormat) && name.Contains(archFilter))
                .ToList();

            if (matches.Any())
            {
                _log?.Invoke($"[llama.cpp] Pattern '{pattern}' matched: {matches[0]}");
                var asset = assetArray.First(a => (a.GetProperty("name").GetString() ?? "") == matches[0]);
                return new DetectedAsset(
                    matches[0], asset.GetProperty("size").GetInt64(),
                    asset.GetProperty("browser_download_url").GetString() ?? "",
                    asset.GetProperty("digest").GetString() ?? "",
                    cudartAssets);
            }
            else
            {
                _log?.Invoke($"[llama.cpp] Pattern '{pattern}' did not match any asset");
            }
        }

        // Final fallback: list available assets for debugging
        var availableAssets = assetArray.Select(a => a.GetProperty("name").GetString() ?? "").ToList();
        _log?.Invoke($"[llama.cpp] No pattern matched. Available {availableAssets.Count} assets: {string.Join(", ", availableAssets)}");

        // Final fallback: any platform-appropriate tar.gz/zip
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            foreach (var asset in assetArray)
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.Contains("-macos-") && name.EndsWith(".tar.gz") && !name.Contains("xcframework"))
                {
                    return new DetectedAsset(
                        name, asset.GetProperty("size").GetInt64(),
                        asset.GetProperty("browser_download_url").GetString() ?? "",
                        asset.GetProperty("digest").GetString() ?? "",
                        cudartAssets);
                }
            }
        }
        else if (isWindows)
        {
            foreach (var asset in assetArray)
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.Contains("-win-cpu-") && name.EndsWith(".zip") && name.Contains(archFilter))
                {
                    return new DetectedAsset(
                        name, asset.GetProperty("size").GetInt64(),
                        asset.GetProperty("browser_download_url").GetString() ?? "",
                        asset.GetProperty("digest").GetString() ?? "",
                        cudartAssets);
                }
            }
        }
        else if (isLinux)
        {
            foreach (var asset in assetArray)
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.Contains("-ubuntu-x64-") && name.EndsWith(".tar.gz"))
                {
                    return new DetectedAsset(
                        name, asset.GetProperty("size").GetInt64(),
                        asset.GetProperty("browser_download_url").GetString() ?? "",
                        asset.GetProperty("digest").GetString() ?? "",
                        cudartAssets);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Check if the current backend is CUDA-based.
    /// </summary>
    private static bool IsCudaBackend()
    {
        return GpuDetectionSettings.GetEffectiveBackend() == GpuDetectionService.GpuBackend.Cuda;
    }


    // ---- CUDA Version-Aware Asset Selection ----

    /// <summary>
    /// Represents a CUDA-related asset parsed from a GitHub release.
    /// </summary>
    internal record CudaAsset(
        string Name,
        string Url,
        long Size,
        string Digest,
        CudaAssetType AssetType,
        string CudaVersion);

    /// <summary>
    /// Type of CUDA asset: llama build or cudart runtime.
    /// </summary>
    internal enum CudaAssetType
    {
        LlamaBuild,
        Cudart
    }

    /// <summary>
    /// Result of asset detection, including the main asset and any associated cudart assets.
    /// </summary>
    internal record DetectedAsset(
        string Name,
        long Size,
        string Url,
        string Digest,
        IReadOnlyList<CudaAsset> CudartAssets);

    /// <summary>
    /// Regex to parse CUDA version from asset names like "llama-cuda12.4-win-cuda-12.4-x64-release.tar.gz".
    /// Captures major.minor (and optional patch) as group 1.
    /// </summary>
    private static readonly Regex CudaAssetVersionRegex = new(
        @"cuda(\d+\.\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Regex to match cudart runtime assets.
    /// Matches patterns like "cudart-cuda12.4-win-12.4-x64.tar.gz" or "cudart-cuda-12.4-ubuntu-x64.tar.gz".
    /// </summary>
    private static readonly Regex CudartAssetRegex = new(
        @"cudart-cuda[\-_](\d+\.\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Regex to match llama CUDA build assets (not cudart).
    /// Matches patterns like "llama-b9670-bin-win-cuda-12.4-x64-release.tar.gz".
    /// </summary>
    private static readonly Regex LlamaCudaBuildRegex = new(
        @"llama\b.*cuda-\d+\.\d+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parse CUDA-related assets from a GitHub release.
    /// Returns both llama CUDA builds and cudart runtime assets.
    /// </summary>
    private List<CudaAsset> ParseCudaAssets(JsonElement release)
    {
        var cudaAssets = new List<CudaAsset>();

        if (!release.TryGetProperty("assets", out var assets))
            return cudaAssets;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";

            // Check for cudart runtime assets
            var cudartMatch = CudartAssetRegex.Match(name);
            if (cudartMatch.Success)
            {
                var cudaVersion = cudartMatch.Groups[1].Value;
                cudaAssets.Add(new CudaAsset(
                    name,
                    asset.GetProperty("browser_download_url").GetString() ?? "",
                    asset.GetProperty("size").GetInt64(),
                    asset.GetProperty("digest").GetString() ?? "",
                    CudaAssetType.Cudart,
                    cudaVersion));
                continue;
            }

            // Check for llama CUDA build assets
            if (LlamaCudaBuildRegex.IsMatch(name) ||
                (name.Contains("cuda", StringComparison.OrdinalIgnoreCase) &&
                 name.Contains("llama", StringComparison.OrdinalIgnoreCase)))
            {
                var versionMatch = CudaAssetVersionRegex.Match(name);
                if (versionMatch.Success)
                {
                    var cudaVersion = versionMatch.Groups[1].Value;
                    cudaAssets.Add(new CudaAsset(
                        name,
                        asset.GetProperty("browser_download_url").GetString() ?? "",
                        asset.GetProperty("size").GetInt64(),
                        asset.GetProperty("digest").GetString() ?? "",
                        CudaAssetType.LlamaBuild,
                        cudaVersion));
                }
            }
        }

        return cudaAssets;
    }

    /// <summary>
    /// Find the best CUDA asset matching the installed CUDA version.
    /// Priority: exact match → same major version → newest available.
    /// </summary>
    internal CudaAsset? FindBestCudaAsset(List<CudaAsset> cudaAssets, string installedCudaVersion)
    {
        if (!cudaAssets.Any())
            return null;

        var installedParts = installedCudaVersion.Split('.');
        var installedMajor = installedParts[0];
        var installedMinor = installedParts.Length > 1 ? installedParts[1] : "0";

        // 1. Exact match
        var exactMatch = cudaAssets.FirstOrDefault(a => a.CudaVersion == installedCudaVersion);
        if (exactMatch != null)
        {
            _log?.Invoke($"[llama.cpp] Found exact CUDA {installedCudaVersion} asset: {exactMatch.Name}");
            return exactMatch;
        }

        // 2. Major version match (e.g. CUDA 12.6 → 12.4)
        var majorMatches = cudaAssets
            .Where(a => a.CudaVersion.Split('.')[0] == installedMajor)
            .ToList();

        if (majorMatches.Any())
        {
            // Pick the highest minor version among major matches
            var best = majorMatches.OrderByDescending(a => ParseVersionComponents(a.CudaVersion)).ToList()[0];
            _log?.Invoke($"[llama.cpp] No exact CUDA match for {installedCudaVersion}, using CUDA {best.CudaVersion} asset: {best.Name}");
            return best;
        }

        // 3. Fallback: newest available
        var newest = cudaAssets.OrderByDescending(a => ParseVersionComponents(a.CudaVersion)).First();
        _log?.Invoke($"[llama.cpp] No CUDA {installedMajor}.x match, using newest available: {newest.Name}");
        return newest;
    }

    /// <summary>
    /// Parse version components for comparison. Returns (major * 1000 + minor, patch).
    /// </summary>
    internal static (int majorMinor, int patch) ParseVersionComponents(string version)
    {
        var parts = version.Split('.');
        var major = int.TryParse(parts[0], out var m) ? m : 0;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
        return (major * 1000 + minor, patch);
    }

    /// <summary>
    /// Download and extract cudart runtime libraries from the matched asset.
    /// Non-critical — failures are logged but don't block installation.
    /// </summary>
}
