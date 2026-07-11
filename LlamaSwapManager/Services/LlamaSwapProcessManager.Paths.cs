using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

public partial class LlamaSwapProcessManager : IDisposable
{
    public void ResolvePaths()
        {
            ExecutablePath = ResolveExecutable();
            ConfigPath = ResolveConfig();
    
            if (ExecutablePath is not null)
                WorkingDirectory = Path.GetDirectoryName(ExecutablePath);
            else if (ConfigPath is not null)
                WorkingDirectory = Path.GetDirectoryName(ConfigPath);
            else
                WorkingDirectory = AppDirectory;
    
            ResolveLlamaServerPath();
        }
    
        /// <summary>
        /// Resolves the llama.cpp binary directory by inspecting the default path
        /// and common installation locations.
        /// M4: Only trusts known directories — no arbitrary PATH resolution.
        /// </summary>
        public void ResolveLlamaServerPath()
        {
            var serverFileName = OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";

            // Check the default ~/.llama/ directory first
            var defaultPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llama");
            if (Directory.Exists(defaultPath))
            {
                // Check if it contains llama-server binary
                var serverPath = Path.Combine(defaultPath, serverFileName);
                if (File.Exists(serverPath))
                {
                    LlamaCppDirectory = defaultPath;
                    return;
                }
            }
    
            // Check ~/.llama-swap/ for llama.cpp
            var swapPath = Path.Combine(UserDirectory, "llama.cpp");
            if (Directory.Exists(swapPath))
            {
                var serverPath = Path.Combine(swapPath, serverFileName);
                if (File.Exists(serverPath))
                {
                    LlamaCppDirectory = swapPath;
                    return;
                }
            }
    
            // Check if llama-server is in the llama-swap app directory
            if (Directory.Exists(AppDirectory))
            {
                var serverPath = Path.Combine(AppDirectory, serverFileName);
                if (File.Exists(serverPath))
                {
                    LlamaCppDirectory = AppDirectory;
                    return;
                }
            }
    
            // M4: Do NOT resolve from PATH — only trust known directories
            // This prevents an attacker from placing a malicious llama-server in PATH
            LlamaCppDirectory = null;
        }
    
        public void RefreshPaths() => ResolvePaths();

        public void DetectApiUrl() => ObserveDetectionAsync(
            DetectApiBaseUrlAsync(),
            value => ApiBaseUrl = value,
            "API URL detection");

        public void DetectLlamaServerUrl() => ObserveDetectionAsync(
            DetectLlamaServerBaseUrlAsync(),
            value => LlamaServerBaseUrl = value,
            "llama-server URL detection");

        /// <summary>
        /// Public alias for dynamic re-detection. Call this from the metrics polling loop
        /// so that when llama-swap switches models (and thus the upstream llama-server port),
        /// the manager picks up the new port automatically.
        /// </summary>
        public void RefreshLlamaServerUrl() => DetectLlamaServerUrl();

        private async void ObserveDetectionAsync(
            Task<string?> detectionTask,
            Action<string?> apply,
            string operation)
        {
            try
            {
                apply(await detectionTask);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                LogMessage?.Invoke($"[manager] {operation} failed: {ex.Message}");
            }
        }
    
        private string? ResolveExecutable()
        {
            var exeSuffix = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
            var candidates = new[]
            {
                Path.Combine(AppDirectory, "llama-swap" + exeSuffix),
                Path.Combine(UserDirectory, "llama-swap" + exeSuffix)
            };
    
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }
    
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                var fullPath = Path.Combine(dir, "llama-swap" + exeSuffix);
                if (File.Exists(fullPath))
                    return fullPath;
            }
    
            return null;
        }
    
        private string? ResolveConfig()
        {
            var candidates = new[]
            {
                // Prefer the real user config. The build output may contain a stale
                // bundled config.yml; using it makes edits appear in preview but vanish
                // after restart because llama-swap/user config remains unchanged.
                Path.Combine(UserDirectory, "config.yml"),
                Path.Combine(AppDirectory, "config.yml")
            };
    
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }
    
            return null;
        }
}
