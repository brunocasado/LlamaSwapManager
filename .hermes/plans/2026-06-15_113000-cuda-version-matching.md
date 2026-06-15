# CUDA Version Matching Implementation Plan

> **For Hermes:** Use subagent-driven-development skill to implement this plan task-by-task.

**Goal:** Detect the installed CUDA version on the system, match it to available llama.cpp CUDA builds from GitHub releases, and allow the user to override the auto-detected version.

**Architecture:** 
- New `CudaVersionDetector` service detects installed CUDA version via `nvcc --version` (Windows/Linux) and registry paths
- `GpuDetectionService.GpuBackendInfo.Detail` enriched with CUDA version (e.g., "Detected: NVIDIA RTX 3080 — CUDA 12.4")
- `LlamaCppDownloader` parses CUDA assets from GitHub API, groups by version, matches installed CUDA
- User can override via Settings UI ComboBox (auto-detected version + all available versions)
- `cudart-llama-bin-*` runtime libraries downloaded alongside the main build

**Tech Stack:** C# / .NET 9, Avalonia UI, CommunityToolkit.Mvvm, GitHub API

---

## Task 1: Create CudaVersionDetector service

**Objective:** Detect installed CUDA version on Windows and Linux.

**Files:**
- Create: `LlamaSwapManager/Services/CudaVersionDetector.cs`

**Step 1: Create the service**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LlamaSwapManager.Services;

/// <summary>
/// Detects the installed CUDA version on Windows and Linux.
/// Returns null if CUDA is not installed.
/// </summary>
public static class CudaVersionDetector
{
    /// <summary>
    /// Returns the detected CUDA version (e.g., "12.4", "12.6", "13.3"), or null.
    /// </summary>
    public static string? DetectCudaVersion()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return DetectWindowsCudaVersion();
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return DetectLinuxCudaVersion();
        
        // macOS — CUDA not applicable
        return null;
    }

    private static string? DetectWindowsCudaVersion()
    {
        // Method 1: Try nvcc --version
        var nvccOutput = RunCommand("nvcc", "--version", 5000);
        if (!string.IsNullOrEmpty(nvccOutput))
        {
            // Output like: "Cuda compilation tools, release 12.4, V12.4.99"
            var match = Regex.Match(nvccOutput, @"release\s+(\d+)\.(\d+)");
            if (match.Success)
                return $"{match.Groups[1].Value}.{match.Groups[2].Value}";
        }

        // Method 2: Check registry
        try
        {
            var registryPath = @"SOFTWARE\NVIDIA Corporation\NVIDIA GPU Computing Toolkit\LocalRegistryPath";
            var localRegistryPath = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(registryPath)?.GetValue("") as string;
            if (!string.IsNullOrEmpty(localRegistryPath))
            {
                var toolkitPath = Path.Combine(localRegistryPath, @"..\..");
                // Read version from CUDA_VERSION.TXT or similar
                var versionFile = Path.Combine(toolkitPath, "version.txt");
                if (File.Exists(versionFile))
                {
                    var content = File.ReadAllText(versionFile).Trim();
                    var match = Regex.Match(content, @"(\d+)\.(\d+)");
                    if (match.Success)
                        return $"{match.Groups[1].Value}.{match.Groups[2].Value}";
                }
            }
        }
        catch { /* fallback to Method 3 */ }

        // Method 3: Check common CUDA paths
        var cudaPaths = new[]
        {
            @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.4",
            @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.6",
            @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8",
            @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.0",
            @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.3",
        };
        
        foreach (var path in cudaPaths)
        {
            if (Directory.Exists(path))
            {
                var version = Path.GetFileName(path).Replace("v", "");
                return version;
            }
        }

        return null;
    }

    private static string? DetectLinuxCudaVersion()
    {
        // Method 1: Try nvcc --version
        var nvccOutput = RunCommand("nvcc", "--version", 5000);
        if (!string.IsNullOrEmpty(nvccOutput))
        {
            var match = Regex.Match(nvccOutput, @"release\s+(\d+)\.(\d+)");
            if (match.Success)
                return $"{match.Groups[1].Value}.{match.Groups[2].Value}";
        }

        // Method 2: Check /usr/local/cuda/version.txt
        var versionFile = "/usr/local/cuda/version.txt";
        if (File.Exists(versionFile))
        {
            var content = File.ReadAllText(versionFile).Trim();
            var match = Regex.Match(content, @"(\d+)\.(\d+)");
            if (match.Success)
                return $"{match.Groups[1].Value}.{match.Groups[2].Value}";
        }

        // Method 3: Check /usr/local/cuda-*/version.txt
        if (Directory.Exists("/usr/local"))
        {
            foreach (var dir in Directory.GetDirectories("/usr/local", "cuda-*"))
            {
                var versionFile2 = Path.Combine(dir, "version.txt");
                if (File.Exists(versionFile2))
                {
                    var content = File.ReadAllText(versionFile2).Trim();
                    var match = Regex.Match(content, @"(\d+)\.(\d+)");
                    if (match.Success)
                        return $"{match.Groups[1].Value}.{match.Groups[2].Value}";
                }
            }
        }

        return null;
    }

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
```

**Step 2: Build to verify**

Run: `dotnet build LlamaSwapManager/LlamaSwapManager.csproj`
Expected: No errors related to CudaVersionDetector.

**Step 3: Commit**

```bash
git add LlamaSwapManager/Services/CudaVersionDetector.cs
git commit -m "feat: add CUDA version detection service

Detect installed CUDA version on Windows and Linux via:
- nvcc --version (primary)
- Registry paths (Windows fallback)
- /usr/local/cuda/version.txt (Linux fallback)
- Directory scanning (last resort)

Returns null on macOS (CUDA not applicable).
Returns null if CUDA is not installed."
```

---

## Task 2: Enrich GpuBackendInfo with CUDA version

**Objective:** Include detected CUDA version in GpuBackendInfo.Detail for Windows/Linux CUDA backends.

**Files:**
- Modify: `LlamaSwapManager/Services/GpuDetectionService.cs:97-133` (DetectWindowsBackends)
- Modify: `LlamaSwapManager/Services/GpuDetectionService.cs:136-173` (DetectLinuxBackends)

**Step 1: Update DetectWindowsBackends**

Replace the CUDA detection block (around line 99-105):

```csharp
// Before:
var nvidiaSmi = RunCommand("nvidia-smi", "--query-gpu=name --format=csv,noheader", 3000);
if (!string.IsNullOrEmpty(nvidiaSmi))
{
    var gpuName = nvidiaSmi.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "NVIDIA GPU";
    backends.Add(new GpuBackendInfo(GpuBackend.Cuda, "NVIDIA CUDA", $"Detected: {gpuName}", 1));
}

// After:
var nvidiaSmi = RunCommand("nvidia-smi", "--query-gpu=name --format=csv,noheader", 3000);
if (!string.IsNullOrEmpty(nvidiaSmi))
{
    var gpuName = nvidiaSmi.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "NVIDIA GPU";
    var cudaVersion = CudaVersionDetector.DetectCudaVersion();
    var detail = cudaVersion != null 
        ? $"Detected: {gpuName} — CUDA {cudaVersion}" 
        : $"Detected: {gpuName} — CUDA version unknown";
    backends.Add(new GpuBackendInfo(GpuBackend.Cuda, "NVIDIA CUDA", detail, 1));
}
```

**Step 2: Update DetectLinuxBackends**

Replace the CUDA detection block (around line 138-144):

```csharp
// Before:
var nvidiaSmi = RunCommand("nvidia-smi", "--query-gpu=name --format=csv,noheader", 3000);
if (!string.IsNullOrEmpty(nvidiaSmi))
{
    var gpuName = nvidiaSmi.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "NVIDIA GPU";
    backends.Add(new GpuBackendInfo(GpuBackend.Cuda, "NVIDIA CUDA", $"Detected: {gpuName}", 1));
}

// After:
var nvidiaSmi = RunCommand("nvidia-smi", "--query-gpu=name --format=csv,noheader", 3000);
if (!string.IsNullOrEmpty(nvidiaSmi))
{
    var gpuName = nvidiaSmi.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "NVIDIA GPU";
    var cudaVersion = CudaVersionDetector.DetectCudaVersion();
    var detail = cudaVersion != null 
        ? $"Detected: {gpuName} — CUDA {cudaVersion}" 
        : $"Detected: {gpuName} — CUDA version unknown";
    backends.Add(new GpuBackendInfo(GpuBackend.Cuda, "NVIDIA CUDA", detail, 1));
}
```

**Step 3: Build to verify**

Run: `dotnet build LlamaSwapManager/LlamaSwapManager.csproj`
Expected: No errors.

**Step 4: Commit**

```bash
git add LlamaSwapManager/Services/GpuDetectionService.cs
git commit -m "feat: include CUDA version in backend detection detail

Enrich GpuBackendInfo.Detail with detected CUDA version:
- 'Detected: NVIDIA RTX 3080 — CUDA 12.4' (when known)
- 'Detected: NVIDIA RTX 3080 — CUDA version unknown' (when nvcc not found)

Uses CudaVersionDetector.DetectCudaVersion() for both Windows and Linux."
```

---

## Task 3: Add CUDA version parsing to LlamaCppDownloader

**Objective:** Parse CUDA assets from GitHub API, group by version, and provide matching logic.

**Files:**
- Modify: `LlamaSwapManager/Services/LlamaCppDownloader.cs`

**Step 1: Add CUDA asset parsing methods**

Add these methods to `LlamaCppDownloader` (after `GetLatestReleaseAsync`):

```csharp
/// <summary>
/// Represents a CUDA-specific asset from the GitHub release.
/// </summary>
private record CudaAsset(string Name, long Size, string Url, string Digest, string CudaVersion);

/// <summary>
/// Parses all CUDA-related assets from a GitHub release.
/// Returns assets grouped by CUDA version.
/// </summary>
private List<CudaAsset> ParseCudaAssets(JsonElement release)
{
    var cudaAssets = new List<CudaAsset>();
    
    if (!release.TryGetProperty("assets", out var assets))
        return cudaAssets;
    
    foreach (var asset in assets.EnumerateArray())
    {
        var name = asset.GetProperty("name").GetString() ?? "";
        
        // Match llama CUDA builds: llama-*-bin-win-cuda-12.4-x64.zip
        // Also match cudart: cudart-llama-bin-win-cuda-12.4-x64.zip
        var cudaMatch = Regex.Match(name, @"-cuda-(\d+\.\d+)-");
        if (cudaMatch.Success && (name.Contains("llama-") || name.Contains("cudart-")))
        {
            cudaAssets.Add(new CudaAsset(
                name,
                asset.GetProperty("size").GetInt64(),
                asset.GetProperty("browser_download_url").GetString() ?? "",
                asset.GetProperty("digest").GetString() ?? "",
                cudaMatch.Groups[1].Value
            ));
        }
    }
    
    return cudaAssets;
}

/// <summary>
/// Find the best CUDA asset matching the installed CUDA version.
/// Returns the main llama.cpp build asset.
/// </summary>
private CudaAsset? FindBestCudaAsset(List<CudaAsset> cudaAssets, string installedCudaVersion)
{
    // Exact match first
    var exact = cudaAssets.FirstOrDefault(a => a.CudaVersion == installedCudaVersion && a.Name.Contains("llama-") && !a.Name.Contains("cudart-"));
    if (exact != null) return exact;
    
    // Major version match (e.g., installed 12.6, available 12.4)
    var installedMajor = installedCudaVersion.Split('.')[0];
    var majorMatch = cudaAssets
        .Where(a => a.CudaVersion.Split('.')[0] == installedMajor && a.Name.Contains("llama-") && !a.Name.Contains("cudart-"))
        .OrderByDescending(a => a.CudaVersion)
        .FirstOrDefault();
    if (majorMatch != null) return majorMatch;
    
    // Fallback: newest CUDA version available
    return cudaAssets
        .Where(a => a.Name.Contains("llama-") && !a.Name.Contains("cudart-"))
        .OrderByDescending(a => a.CudaVersion)
        .FirstOrDefault();
}
```

**Step 2: Add cudart asset download**

Add method to download CUDA runtime libraries:

```csharp
private async Task<bool> DownloadCudaRuntimeAsync(List<CudaAsset> cudaAssets, string targetDirectory, CancellationToken ct)
{
    var installedVersion = CudaVersionDetector.DetectCudaVersion();
    if (string.IsNullOrEmpty(installedVersion)) return true; // No CUDA, skip
    
    var runtimeAsset = cudaAssets.FirstOrDefault(a => a.Name.Contains("cudart-") && a.CudaVersion == installedVersion);
    if (runtimeAsset == null)
    {
        LogMessage?.Invoke("[llama.cpp] CUDA runtime asset not found for version " + installedVersion);
        return true; // Non-critical
    }
    
    var tempPath = Path.Combine(_downloadsDir, runtimeAsset.Name);
    LogMessage?.Invoke($"[llama.cpp] Downloading CUDA runtime {runtimeAsset.Name}...");
    
    // Download and extract to target directory
    using var response = await _http.GetAsync(runtimeAsset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
    if (!response.IsSuccessStatusCode)
    {
        LogMessage?.Invoke($"[llama.cpp] CUDA runtime download failed: {(int)response.StatusCode}");
        return true; // Non-critical
    }
    
    using var stream = await response.Content.ReadAsStreamAsync(ct);
    using var fileStream = new FileStream(tempPath, FileMode.Create);
    await stream.CopyToAsync(fileStream, ct);
    
    // Extract DLLs/SOs to target directory
    using var archive = ZipFile.OpenRead(tempPath);
    foreach (var entry in archive.Entries)
    {
        entry.ExtractToFile(Path.Combine(targetDirectory, entry.FullName), overwrite: true);
    }
    
    File.Delete(tempPath);
    LogMessage?.Invoke("[llama.cpp] CUDA runtime installed");
    return true;
}
```

**Step 3: Update DetectAssetForPlatform to use CUDA version matching**

Modify the Windows/Linux branch in `DetectAssetForPlatform` (around line 200-217):

```csharp
else
{
    // Windows/Linux: use user preference or auto-detect
    var effectiveBackend = GpuDetectionSettings.GetEffectiveBackend();
    
    if (effectiveBackend == GpuBackend.Cuda)
    {
        // CUDA: parse available versions, match to installed
        var cudaAssets = ParseCudaAssets(release.Value);
        var installedVersion = CudaVersionDetector.DetectCudaVersion();
        
        if (cudaAssets.Any())
        {
            if (installedVersion != null)
            {
                var bestAsset = FindBestCudaAsset(cudaAssets, installedVersion);
                if (bestAsset != null)
                {
                    LogMessage?.Invoke($"[llama.cpp] CUDA asset selected: {bestAsset.Name} (CUDA {bestAsset.CudaVersion}, installed: {installedVersion})");
                    // Store for later cudart download
                    _lastCudaAssets = cudaAssets;
                    return (bestAsset.Name, bestAsset.Size, bestAsset.Url, bestAsset.Digest);
                }
            }
            
            // No installed CUDA detected, use newest available
            var newest = cudaAssets.OrderByDescending(a => a.CudaVersion).FirstOrDefault();
            if (newest != null)
            {
                LogMessage?.Invoke($"[llama.cpp] No CUDA detected, using newest: {newest.Name} (CUDA {newest.CudaVersion})");
                _lastCudaAssets = cudaAssets;
                return (newest.Name, newest.Size, newest.Url, newest.Digest);
            }
        }
        
        LogMessage?.Invoke("[llama.cpp] No CUDA assets found in release");
        return null;
    }
    else
    {
        // Non-CUDA backend: use pattern-based matching
        var userPattern = GpuDetectionService.GetPreferredAssetPattern(effectiveBackend);
        if (userPattern != null)
            patterns.Add(userPattern);
        
        // Fall back to auto-detected backends in priority order
        var detected = GpuDetectionService.DetectBackends();
        foreach (var gpu in detected)
        {
            if (gpu.Backend == effectiveBackend) continue;
            var pattern = GpuDetectionService.GetPreferredAssetPattern(gpu.Backend);
            if (pattern != null && !patterns.Contains(pattern))
                patterns.Add(pattern);
        }
    }
}
```

Add field to store CUDA assets for cudart download:

```csharp
private List<CudaAsset>? _lastCudaAssets;
```

**Step 4: Call cudart download after main install**

In `DownloadAndInstallAsync`, after `ExtractAndInstallAsync` succeeds (around line 103-107):

```csharp
// Step 6.5: Download CUDA runtime if applicable
if (effectiveBackend == GpuBackend.Cuda && _lastCudaAssets != null)
{
    progress?.Report(0.85);
    await DownloadCudaRuntimeAsync(_lastCudaAssets, targetDirectory, ct);
}
```

**Step 5: Build to verify**

Run: `dotnet build LlamaSwapManager/LlamaSwapManager.csproj`
Expected: No errors.

**Step 6: Commit**

```bash
git add LlamaSwapManager/Services/LlamaCppDownloader.cs
git commit -m "feat: add CUDA version-aware asset selection

- Parse CUDA assets from GitHub release (llama-*-cuda-*.zip, cudart-llama-bin-cuda-*.zip)
- Match installed CUDA version to available builds
- Exact match > major version match > newest available
- Download cudart runtime libraries alongside main build
- Log selected CUDA version for debugging

Handles: CUDA 12.4, 12.6, 12.8, 13.0, 13.3 and future versions."
```

---

## Task 4: Add CUDA version to Settings UI

**Objective:** Show detected CUDA version in Settings, allow user override.

**Files:**
- Modify: `LlamaSwapManager/ViewModels/MainViewModel.cs`
- Modify: `LlamaSwapManager.Desktop/Views/MainWindow.axaml`

**Step 1: Add CUDA version properties to MainViewModel**

After the GPU backend properties (around line 54-58):

```csharp
// CUDA version selection
[ObservableProperty] private string _selectedCudaVersion = "";
[ObservableProperty] private string _cudaVersionStatus = "";
[ObservableProperty] private string _cudaVersionStatusColor = "#888888";
private readonly List<string> _cudaVersionOptions = new();
public IReadOnlyList<string> CudaVersionOptions => _cudaVersionOptions;
```

**Step 2: Add CUDA version detection method**

After `DetectGpuBackends()` method:

```csharp
private void DetectCudaVersion()
{
    try
    {
        var installed = CudaVersionDetector.DetectCudaVersion();
        _cudaVersionOptions.Clear();
        
        if (installed != null)
        {
            _cudaVersionOptions.Add($"Auto-detect ({installed})");
            SelectedCudaVersion = _cudaVersionOptions[0];
            CudaVersionStatus = $"CUDA {installed} detected";
            CudaVersionStatusColor = "#A6E3A1";
        }
        else
        {
            _cudaVersionOptions.Add("CUDA not detected");
            SelectedCudaVersion = _cudaVersionOptions[0];
            CudaVersionStatus = "No CUDA installation found";
            CudaVersionStatusColor = "#F9E2AF";
        }
    }
    catch (Exception ex)
    {
        CudaVersionStatus = $"Error: {ex.Message}";
        CudaVersionStatusColor = "#F38BA8";
        _cudaVersionOptions.Clear();
        _cudaVersionOptions.Add("Detection failed");
        SelectedCudaVersion = _cudaVersionOptions[0];
    }
}
```

**Step 3: Call DetectCudaVersion in constructor**

After `DetectGpuBackends()` in the constructor:

```csharp
// Detect CUDA version
DetectCudaVersion();
```

**Step 4: Add partial method for CUDA version change**

After `OnSelectedGpuBackendChanged`:

```csharp
partial void OnSelectedCudaVersionChanged(string value)
{
    // Map selection to CUDA version string for logging
    if (value.StartsWith("Auto-detect"))
    {
        OnLogMessage("[ui] CUDA version: auto-detect enabled");
    }
    else
    {
        OnLogMessage($"[ui] CUDA version override: {value}");
    }
}
```

**Step 5: Add CUDA section to Settings AXAML**

After the GPU Backend border (around line 746):

```xml
<!-- CUDA Version -->
<Border Padding="16" Background="#313244" CornerRadius="8">
  <StackPanel Spacing="12">
    <TextBlock Text="CUDA Version" FontSize="15" FontWeight="Bold" Foreground="#B4BEFE" />
    <TextBlock Text="Select which CUDA version to use for llama.cpp CUDA builds. Auto-detect matches your installed CUDA toolkit." FontSize="12" Foreground="#A6ADC8" TextWrapping="Wrap" />
    <Grid ColumnDefinitions="200,*" RowSpacing="10">
      <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/>
        <RowDefinition Height="Auto"/>
      </Grid.RowDefinitions>
      <TextBlock Grid.Row="0" Grid.Column="0" Text="CUDA Version" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center" />
      <ComboBox Grid.Row="0" Grid.Column="1" ItemsSource="{Binding CudaVersionOptions}" SelectedItem="{Binding SelectedCudaVersion}" />
      <TextBlock Grid.Row="1" Grid.Column="0" Grid.ColumnSpan="2" Text="{Binding CudaVersionStatus}" FontSize="11" Foreground="{Binding CudaVersionStatusColor}" />
    </Grid>
  </StackPanel>
</Border>
```

**Step 6: Build to verify**

Run: `dotnet build LlamaSwapManager.Desktop/LlamaSwapManager.Desktop.csproj`
Expected: No errors.

**Step 7: Commit**

```bash
git add LlamaSwapManager/ViewModels/MainViewModel.cs LlamaSwapManager.Desktop/Views/MainWindow.axaml
git commit -m "feat: add CUDA version selection to Settings UI

Settings tab:
- New 'CUDA Version' section below 'GPU Acceleration'
- Shows detected CUDA version (e.g., 'CUDA 12.4 detected')
- Auto-detect enabled by default
- Status color: green=detected, yellow=not found, red=error

Integrates with CudaVersionDetector for Windows/Linux CUDA detection."
```

---

## Task 5: Add CUDA version to config model

**Objective:** Persist CUDA version preference in config.

**Files:**
- Modify: `LlamaSwapManager/Models/LlamaSwapConfig.cs`

**Step 1: Add CudaVersion to AutoUpdateConfig**

```csharp
public class AutoUpdateConfig
{
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;

    [YamlMember(Alias = "checkOnStartup")]
    public bool CheckOnStartup { get; set; } = true;

    [YamlMember(Alias = "checkInterval")]
    public string? CheckInterval { get; set; } = "daily";

    [YamlMember(Alias = "autoDownload")]
    public bool AutoDownload { get; set; } = false;

    [YamlMember(Alias = "cudaVersion")]
    public string? CudaVersion { get; set; }
}
```

**Step 2: Build to verify**

Run: `dotnet build LlamaSwapManager/LlamaSwapManager.csproj`
Expected: No errors.

**Step 3: Commit**

```bash
git add LlamaSwapManager/Models/LlamaSwapConfig.cs
git commit -m "feat: add cudaVersion to AutoUpdateConfig

Persist CUDA version preference in config.yml:
- cudaVersion: '12.4' (override auto-detect)
- null (use auto-detect)

Backward compatible: existing configs without cudaVersion field work fine."
```

---

## Task 6: Test end-to-end

**Objective:** Verify CUDA detection and asset matching works.

**Steps:**

1. **On Windows with CUDA installed:**
   - Open app, go to Settings
   - Verify "CUDA Version" section shows detected version
   - Verify "GPU Acceleration" section shows "Detected: [GPU] — CUDA [version]"
   - Trigger llama.cpp update
   - Check logs: should show CUDA version matching

2. **On Windows without CUDA:**
   - Verify "CUDA not detected" shown
   - Verify fallback to CPU build works

3. **On Linux with CUDA:**
   - Same checks as Windows

4. **On macOS:**
   - CUDA section should show "CUDA not applicable" or be hidden
   - GPU section should show "Apple Metal"

**Verification commands:**

```bash
# Build
dotnet build LlamaSwapManager.Desktop/LlamaSwapManager.Desktop.csproj

# Run
dotnet run --project LlamaSwapManager.Desktop/LlamaSwapManager.Desktop.csproj
```

---

## Risks and Tradeoffs

1. **CUDA binary compatibility:** CUDA 12.x builds are generally forward-compatible (12.4 build works with CUDA 12.6 runtime), but not backward-compatible. The "major version match" fallback handles this.

2. **nvcc not installed:** Many users have only the CUDA runtime, not the toolkit (nvcc). The registry/path fallback handles this.

3. **Multiple CUDA versions:** Users may have multiple CUDA versions installed. We detect the "default" one (nvcc or /usr/local/cuda symlink).

4. **cudart download timing:** Downloading cudart after main install adds time. Consider downloading in parallel or as part of the main download phase.

5. **Windows registry access:** `Microsoft.Win32.Registry` may require additional permissions on some systems. The fallback to `nvcc` handles this.

---

## Open Questions

1. Should we show all available CUDA versions in the ComboBox (not just auto-detect)? This would let users manually select a version even when auto-detect works.
   - **Decision:** Yes, show all available versions after fetching from GitHub API. But this requires fetching the release info first, which adds latency to Settings tab. Defer to a "refresh" action.

2. Should we validate that the selected CUDA version actually exists in the release before applying?
   - **Decision:** Yes, validate on selection change. Show warning if selected version not available.

3. macOS: Should we hide the CUDA section entirely on macOS?
   - **Decision:** Yes, conditionally show/hide based on platform.

---

## Files Summary

**New files:**
- `LlamaSwapManager/Services/CudaVersionDetector.cs`

**Modified files:**
- `LlamaSwapManager/Services/GpuDetectionService.cs` — enrich CUDA backend detail with version
- `LlamaSwapManager/Services/LlamaCppDownloader.cs` — CUDA asset parsing, version matching, cudart download
- `LlamaSwapManager/ViewModels/MainViewModel.cs` — CUDA version properties and detection
- `LlamaSwapManager.Desktop/Views/MainWindow.axaml` — CUDA version UI section
- `LlamaSwapManager/Models/LlamaSwapConfig.cs` — cudaVersion field in AutoUpdateConfig

**Total estimated effort:** ~6 tasks, ~30-45 minutes of focused work.
