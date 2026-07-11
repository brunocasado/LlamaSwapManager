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
    // --- Metrics polling ---
    private void StartMetricsPolling()
    {
        StopMetricsPolling();

        // Use llama-server URL for metrics (not llama-swap which has different metrics)
        var baseUrl = _processManager.LlamaServerBaseUrl ?? _processManager.DetectedApiBaseUrl;
        if (string.IsNullOrEmpty(baseUrl)) return;

        _metricsService = new MetricsService(new HttpClient { Timeout = TimeSpan.FromSeconds(2) });
        _metricsService.SetApiBaseUrl(baseUrl);
        _metricsCts = new CancellationTokenSource();
        _ = PollMetricsAsync(_metricsCts.Token);
    }

    private void StopMetricsPolling()
    {
        _metricsCts?.Cancel();
        _metricsCts?.Dispose();
        _metricsCts = null;
        _metricsService = null;
    }

    // --- Log streaming (llama-swap /logs/stream/upstream) ---
    // The SSE endpoint is generic (not model-specific). We keep a single persistent
    // connection alive and only reconnect if it actually dies.
    private async Task StartLogStreamingAsync()
    {
        var baseUrl = _processManager.DetectedApiBaseUrl;
        if (string.IsNullOrEmpty(baseUrl))
            return;

        // If the stream is already alive on the same URL, do nothing.
        if (_logStreamService?.IsRunning == true)
            return;

        // Stream is dead or first start — create a new connection.
        if (_logStreamService != null)
        {
            _logStreamService.LogBatchReceived -= OnUpstreamLogBatchReceived;
            _logStreamService.Dispose();
            _logStreamService = null;
        }
        _logStreamCts?.Cancel();
        _logStreamCts?.Dispose();
        _logStreamCts = null;

        _logStreamService = new LogStreamService(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            baseUrl);
        _logStreamService.LogBatchReceived += OnUpstreamLogBatchReceived;
        _logStreamCts = new CancellationTokenSource();

        try
        {
            await _logStreamService.StartAsync("upstream", _logStreamCts.Token);
        }
        catch { /* stream may fail if API is temporarily unavailable */ }
    }

    private async Task StopLogStreamingAsync()
    {
        if (_logStreamService != null)
        {
            _logStreamService.LogBatchReceived -= OnUpstreamLogBatchReceived;
            await _logStreamService.StopAsync();
            _logStreamService.Dispose();
            _logStreamService = null;
        }
        _logStreamCts?.Cancel();
        _logStreamCts?.Dispose();
        _logStreamCts = null;
    }

    /// <summary>
    /// Receives a batch of upstream log lines from the background stream task.
    /// Posts a single UI update for the entire batch — not one per line.
    /// </summary>
    private void OnUpstreamLogBatchReceived(IReadOnlyList<string> batch)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnUpstreamLogBatchReceived(batch));
            return;
        }

        // Extract real-time tokens/sec from upstream log lines and push to UI immediately
        foreach (var line in batch)
        {
            var match = s_tpsRegex.Match(line);
            if (match.Success && double.TryParse(match.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var tps))
            {
                TokensPerSecond = tps;
            }
        }

        // Enqueue all lines
        foreach (var line in batch)
        {
            _upstreamLogLines.Enqueue(line);
        }
        while (_upstreamLogLines.Count > MaxLogLines) _upstreamLogLines.Dequeue();

        UpdateFilteredLogTexts(updateUpstream: true);
    }

    private void UpdateFilteredLogTexts(bool updateUpstream = true)
    {
        if (updateUpstream)
        {
            // Take only the last MaxDisplayLines to keep UI string allocation bounded
            var displayLines = _upstreamLogLines.Count > MaxDisplayLines
                ? _upstreamLogLines.Skip(_upstreamLogLines.Count - MaxDisplayLines)
                : _upstreamLogLines;

            if (string.IsNullOrWhiteSpace(UpstreamLogFilterText))
            {
                UpstreamLogText = string.Join("\n", displayLines);
            }
            else
            {
                try
                {
                    var regex = GetOrBuildRegex(ref _cachedUpstreamRegex, ref _cachedUpstreamRegexText, UpstreamLogFilterText);
                    var filtered = new List<string>();
                    foreach (var line in displayLines)
                    {
                        if (regex.IsMatch(line))
                            filtered.Add(line);
                    }
                    UpstreamLogText = string.Join("\n", filtered);
                }
                catch
                {
                    UpstreamLogText = string.Join("\n", displayLines);
                }
            }
        }
    }

    private static Regex GetOrBuildRegex(ref Regex? cached, ref string? cachedText, string pattern)
    {
        if (cached is not null && cachedText == pattern)
            return cached;

        cachedText = pattern;
        cached = new Regex(pattern, RegexOptions.Compiled | RegexOptions.Multiline);
        return cached;
    }

    partial void OnUpstreamLogFilterTextChanged(string value)
    {
        UpdateFilteredLogTexts();
    }

     partial void OnSelectedGpuBackendChanged(string value)
    {
        // Map display name back to GpuBackend enum
        var backends = GpuDetectionSettings.GetAvailableBackends();
        var newBackend = backends.FirstOrDefault(b => $"{b.Name} ({b.Detail})" == SelectedGpuBackend);

        if (newBackend.Backend != GpuDetectionService.GpuBackend.CpuOnly)
        {
            GpuDetectionSettings.PreferredBackend = newBackend.Backend;
            OnLogMessage($"[ui] GPU backend set to: {newBackend.Name}");
        }
        else
        {
            GpuDetectionSettings.PreferredBackend = null; // reset to auto-detect
            OnLogMessage("[ui] GPU backend reset to auto-detect (CPU fallback)");
        }

        UpdateCudaVersionVisibility();
    }

    private async Task PollMetricsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _metricsService != null)
        {
            try
            {
                // Re-detect the llama-server port on each poll (fire-and-forget, non-blocking)
                _processManager.RefreshLlamaServerUrl();
                var baseUrl = _processManager.LlamaServerBaseUrl ?? _processManager.DetectedApiBaseUrl;
                if (!string.IsNullOrEmpty(baseUrl) && baseUrl != _metricsService.ApiBaseUrl)
                {
                    _metricsService.SetApiBaseUrl(baseUrl);
                }

                var metrics = await _metricsService.GetMetricsAsync();
                if (metrics != null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        PrefillTokens = (long)metrics.PromptTokens;
                        DecodeTokens = (long)metrics.EvalTokens;
                        ActiveSlots = metrics.ActiveSlots;
                        // Idle fallback only — live TPS comes from upstream print_timing logs.
                        if (TokensPerSecond <= 0 && metrics.TokensPerSecond > 0)
                            TokensPerSecond = metrics.TokensPerSecond;
                    });

                    // Refresh loaded models
                    RefreshLoadedModelsAsync();
                }

                // Retry starting the upstream log stream on each poll cycle.
                // The stream may not start immediately if no model is loaded yet —
                // this ensures it kicks in as soon as a model becomes ready.
                await StartLogStreamingAsync();
            }
            catch { /* ignore polling errors */ }

            await Task.Delay(2000, ct);
        }
    }
}
