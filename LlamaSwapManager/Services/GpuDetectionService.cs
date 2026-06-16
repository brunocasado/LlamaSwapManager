using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

/// <summary>
/// Detects available GPU backends on the current platform.
/// Returns a prioritized list of supported backends based on hardware and drivers.
/// </summary>
public static class GpuDetectionService
{
    public enum GpuBackend
    {
        /// <summary>
        /// No GPU detected — CPU-only fallback.
        /// </summary>
        CpuOnly,

        /// <summary>
        /// NVIDIA CUDA (Linux/Windows).
        /// </summary>
        Cuda,

        /// <summary>
        /// AMD ROCm (Linux).
        /// </summary>
        Rocm,

        /// <summary>
        /// Vulkan (cross-platform, works with NVIDIA/AMD/Intel).
        /// </summary>
        Vulkan,

        /// <summary>
        /// Intel SYCL (oneAPI).
        /// </summary>
        Sycl,

        /// <summary>
        /// OpenCL (fallback for older GPUs).
        /// </summary>
        Opencl,

        /// <summary>
        /// Apple Metal (macOS only, always available on modern macOS).
        /// </summary>
        Metal
    }

    /// <summary>
    /// Information about a detected GPU backend.
    /// </summary>
    public record GpuBackendInfo(
        GpuBackend Backend,
        string Name,
        string? Detail,
        int Priority
    );

    /// <summary>
    /// Detects all available GPU backends on the current platform.
    /// Returns a prioritized list (highest priority first).
    /// </summary>
    public static IReadOnlyList<GpuBackendInfo> DetectBackends()
    {
        var backends = new List<GpuBackendInfo>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            backends.Add(new GpuBackendInfo(GpuBackend.Metal, "Apple Metal", "macOS GPU acceleration (built-in)", 1));
            backends.Add(new GpuBackendInfo(GpuBackend.CpuOnly, "CPU Only", "Fallback if Metal fails", 10));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            DetectWindowsBackends(backends);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            DetectLinuxBackends(backends);
        }

        // Ensure at least CPU fallback
        if (!backends.Any(b => b.Backend == GpuBackend.CpuOnly))
        {
            backends.Add(new GpuBackendInfo(GpuBackend.CpuOnly, "CPU Only", "Fallback if GPU fails", 10));
        }

        return backends;
    }

    private static void DetectWindowsBackends(List<GpuBackendInfo> backends)
    {
        // 1. NVIDIA CUDA — check nvidia-smi
        var nvidiaSmi = RunCommand("nvidia-smi", "--query-gpu=name --format=csv,noheader", 3000);
        if (!string.IsNullOrEmpty(nvidiaSmi))
        {
            var gpuName = nvidiaSmi.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "NVIDIA GPU";
            var cudaVersion = CudaVersionDetector.GetCudaVersion();
            var detail = cudaVersion != null ? $"Detected: {gpuName} (CUDA {cudaVersion})" : $"Detected: {gpuName}";
            backends.Add(new GpuBackendInfo(GpuBackend.Cuda, "NVIDIA CUDA", detail, 1));
        }

        // 2. AMD ROCm — check if rocm-smi exists
        var rocmPaths = new[] { "C:\\Program Files\\AMD\\ROCm\\bin\\rocm-smi.exe", "C:\\Program Files\\AMD\\ROCm\\bin\\rocminfo.exe" };
        if (rocmPaths.Any(p => File.Exists(p)))
        {
            backends.Add(new GpuBackendInfo(GpuBackend.Rocm, "AMD ROCm", "ROCm detected (AMD GPU)", 2));
        }

        // 3. Vulkan — check for vulkaninfo or vulkan.dll
        var vulkanInfo = RunCommand("vulkaninfo", "--summary", 3000);
        if (!string.IsNullOrEmpty(vulkanInfo) || File.Exists("C:\\Windows\\System32\\vulkan-1.dll"))
        {
            backends.Add(new GpuBackendInfo(GpuBackend.Vulkan, "Vulkan", "Cross-platform GPU acceleration", 3));
        }

        // 4. Intel SYCL — check for sycl-ls
        var syclLs = RunCommand("sycl-ls", "", 3000);
        if (!string.IsNullOrEmpty(syclLs))
        {
            backends.Add(new GpuBackendInfo(GpuBackend.Sycl, "Intel SYCL", "oneAPI GPU acceleration", 4));
        }

        // 5. OpenCL — check for OpenCL
        var clinfo = RunCommand("clinfo", "", 3000);
        if (!string.IsNullOrEmpty(clinfo) || Directory.Exists("C:\\Program Files\\Common Files\\Intel\\OpenCL"))
        {
            backends.Add(new GpuBackendInfo(GpuBackend.Opencl, "OpenCL", "Fallback GPU acceleration", 5));
        }
    }

    private static void DetectLinuxBackends(List<GpuBackendInfo> backends)
    {
        // 1. NVIDIA CUDA — check nvidia-smi
        var nvidiaSmi = RunCommand("nvidia-smi", "--query-gpu=name --format=csv,noheader", 3000);
        if (!string.IsNullOrEmpty(nvidiaSmi))
        {
            var gpuName = nvidiaSmi.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "NVIDIA GPU";
            var cudaVersion = CudaVersionDetector.GetCudaVersion();
            var detail = cudaVersion != null ? $"Detected: {gpuName} (CUDA {cudaVersion})" : $"Detected: {gpuName}";
            backends.Add(new GpuBackendInfo(GpuBackend.Cuda, "NVIDIA CUDA", detail, 1));
        }

        // 2. AMD ROCm — check for rocm-smi or /opt/rocm
        var rocmExists = File.Exists("/opt/rocm/bin/rocm-smi") || File.Exists("/opt/rocm/bin/rocm-smi.exe");
        var rocmInfo = RunCommand("rocm-smi", "", 3000);
        if (rocmExists || !string.IsNullOrEmpty(rocmInfo))
        {
            backends.Add(new GpuBackendInfo(GpuBackend.Rocm, "AMD ROCm", "AMD GPU acceleration", 2));
        }

        // 3. Vulkan — check for vulkaninfo
        var vulkanInfo = RunCommand("vulkaninfo", "--summary", 3000);
        if (!string.IsNullOrEmpty(vulkanInfo) || File.Exists("/usr/lib/x86_64-linux-gnu/libvulkan.so"))
        {
            backends.Add(new GpuBackendInfo(GpuBackend.Vulkan, "Vulkan", "Cross-platform GPU acceleration", 3));
        }

        // 4. Intel SYCL — check for sycl-ls
        var syclLs = RunCommand("sycl-ls", "", 3000);
        if (!string.IsNullOrEmpty(syclLs))
        {
            backends.Add(new GpuBackendInfo(GpuBackend.Sycl, "Intel SYCL", "oneAPI GPU acceleration", 4));
        }

        // 5. OpenCL — check for clinfo or /usr/lib/OpenCL
        var clinfo = RunCommand("clinfo", "", 3000);
        if (!string.IsNullOrEmpty(clinfo) || Directory.Exists("/usr/lib/OpenCL"))
        {
            backends.Add(new GpuBackendInfo(GpuBackend.Opencl, "OpenCL", "Fallback GPU acceleration", 5));
        }
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

    /// <summary>
    /// Given a detected GPU backend and the current OS/arch, returns the preferred asset name pattern.
    /// Returns null if no GPU-specific asset matches.
    /// </summary>
    public static string? GetPreferredAssetPattern(GpuBackend backend)
    {
        var isMacArm = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
                       RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        var isMacIntel = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
                         RuntimeInformation.ProcessArchitecture == Architecture.X64;
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        if (isMacArm)
        {
            // macOS ARM64: Metal is built into the arm64 build
            return "-macos-arm64-";
        }

        if (isMacIntel)
        {
            return "-macos-x64-";
        }

        if (isWindows)
        {
            return backend switch
            {
                GpuBackend.Cuda => "-win-cuda-12.4-x64-",
                GpuBackend.Rocm => "-win-hip-radeon-x64-",
                GpuBackend.Vulkan => "-win-vulkan-x64-",
                GpuBackend.Sycl => "-win-sycl-x64-",
                GpuBackend.Opencl => "-win-opencl-adreno-arm64-",
                GpuBackend.CpuOnly => "-win-cpu-x64-",
                _ => "-win-cpu-x64-"
            };
        }

        if (isLinux)
        {
            return backend switch
            {
                GpuBackend.Cuda => "-ubuntu-cuda-",
                GpuBackend.Rocm => "-ubuntu-rocm-",
                GpuBackend.Vulkan => "-ubuntu-vulkan-x64-",
                GpuBackend.Sycl => "-ubuntu-sycl-",
                GpuBackend.Opencl => "-ubuntu-opencl-",
                GpuBackend.CpuOnly => "-ubuntu-x64-",
                _ => "-ubuntu-x64-"
            };
        }

        return null;
    }
}
