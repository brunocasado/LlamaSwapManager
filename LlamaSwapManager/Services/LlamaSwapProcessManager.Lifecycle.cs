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
    public async Task<bool> StartAsync()
        {
            ResolvePaths();
            LogProcessSnapshot("start requested");
            var existingApi = await DetectApiBaseUrlAsync();
            if (Status == LlamaSwapStatus.Running || existingApi is not null)
            {
                ApiBaseUrl = existingApi;
                SetStatus(LlamaSwapStatus.Running);
                LogMessage?.Invoke($"[manager] llama-swap already running (API: {ApiBaseUrl})");
                return true;
            }
    
            if (ExecutablePath is null)
            {
                LogMessage?.Invoke("llama-swap executable not found");
                return false;
            }
    
            if (ConfigPath is null)
            {
                LogMessage?.Invoke("config.yml not found");
                return false;
            }
    
            SetStatus(LlamaSwapStatus.Starting);
            LogMessage?.Invoke($"[manager] starting llama-swap: exe={ExecutablePath} config={ConfigPath}");
    
            try
            {
                // Remove the Windows downloaded-file marker without invoking PowerShell.
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        File.Delete(ExecutablePath + ":Zone.Identifier");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                    {
                        LogMessage?.Invoke($"[manager] unblock warning: {ex.Message}");
                    }
                }

                var psiStart = new ProcessStartInfo
                {
                    FileName = ExecutablePath,
                    WorkingDirectory = WorkingDirectory ?? AppDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psiStart.ArgumentList.Add("--config");
                psiStart.ArgumentList.Add(ConfigPath);

                _process = new Process { StartInfo = psiStart, EnableRaisingEvents = true };
                _process.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) LogMessage?.Invoke($"[out] {e.Data}"); };
                _process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) LogMessage?.Invoke($"[err] {e.Data}"); };
                _process.Exited += OnProcessExited;
    
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
    
                // H3: Track managed PID
                lock (_managedPids)
                {
                    _managedPids.Add(_process.Id);
                }
    
                var apiReady = await WaitForApiReadyAsync(15);
    
                if (apiReady)
                {
                    ApiBaseUrl = await DetectApiBaseUrlAsync();
                    SetStatus(LlamaSwapStatus.Running);
                    LogMessage?.Invoke($"[manager] llama-swap started (API: {ApiBaseUrl})");
                    return true;
                }
                else
                {
                    SetStatus(LlamaSwapStatus.Error);
                    LogProcessSnapshot("API not responding after start");
                    LogMessage?.Invoke("[manager] llama-swap started but API not responding");
                    return false;
                }
            }
            catch (Exception ex)
            {
                SetStatus(LlamaSwapStatus.Error);
                LogMessage?.Invoke($"Error starting: {ex.Message}");
                return false;
            }
        }
    
        public Task<bool> StartLlamaSwapAsync() => StartAsync();
        public Task<bool> StopLlamaSwapAsync() => StopAsync();
        public Task<bool> RestartLlamaSwapAsync() => RestartAsync();
}
