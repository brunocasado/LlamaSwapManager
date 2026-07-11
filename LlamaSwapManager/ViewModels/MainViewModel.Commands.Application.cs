using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaSwapManager.Models;
using LlamaSwapManager.Services;

namespace LlamaSwapManager.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public async Task QuitApplicationAsync()
        {
            OnLogMessage("[ui] quit requested");
            try
            {
                // Always try stop when any llama process exists, not only when enum says Running.
                if (_processManager.IsRunning()
                    || _processManager.Status is LlamaSwapStatus.Running or LlamaSwapStatus.Starting or LlamaSwapStatus.Stopping)
                {
                    try
                    {
                        await ExecuteStopAsync().WaitAsync(TimeSpan.FromSeconds(12));
                    }
                    catch (TimeoutException)
                    {
                        OnLogMessage("[ui] quit: stop timed out — force killing processes");
                        try { await _processManager.ForceStopForQuitAsync(); } catch { }
                    }
                }
                else
                {
                    // Still clear orphans that may exist without matching Status enum.
                    try { await _processManager.ForceStopForQuitAsync(); } catch { }
                }
            }
            catch (Exception ex)
            {
                OnLogMessage($"[ui] quit stop path error: {ex.Message}");
                try { await _processManager.ForceStopForQuitAsync(); } catch { }
            }
            finally
            {
                try
                {
                    StopMetricsPolling();
                    await StopLogStreamingAsync().WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch { }
    
                try
                {
                    if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                    {
                        // Let window Closing complete (do not re-hide to tray).
                        try
                        {
                            var win = desktop.MainWindow;
                            win?.GetType().GetMethod("BeginExit")?.Invoke(win, null);
                        }
                        catch { }
    
                        desktop.Shutdown(0);
                    }
                }
                catch (Exception ex)
                {
                    OnLogMessage($"[ui] desktop.Shutdown failed: {ex.Message}");
                }
    
                // Hard guarantee: Avalonia/tray can leave the process alive after Shutdown.
                // Window X still only hides — this runs ONLY on explicit Quit.
                try { Environment.Exit(0); } catch { }
            }
        }
    
    
        private void RefreshLoadedModelsAsync()
        {
            var baseUrl = _processManager.ApiBaseUrl;
            if (string.IsNullOrEmpty(baseUrl)) return;
    
            _ = Task.Run(async () =>
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                    var response = await http.GetAsync($"{baseUrl}/running");
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var data = System.Text.Json.JsonSerializer.Deserialize<RunningResponse>(json);
                        if (data != null)
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                LoadedModels.Clear();
                                foreach (var m in data.Running)
                                    LoadedModels.Add(m);
                            });
                        }
                    }
                }
                catch { /* ignore — models refresh silently */ }
            });
        }
}
