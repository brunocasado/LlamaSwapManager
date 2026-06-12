using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using LlamaSwapManager.Services;

namespace LlamaSwapManager.ViewModels;

public partial class MetricsViewModel : ObservableObject, IDisposable
{
    private readonly MetricsService _metricsService;
    private readonly string? _apiBaseUrl;
    private bool _isRunning;

    [ObservableProperty] private double _promptTokens;
    [ObservableProperty] private double _evalTokens;
    [ObservableProperty] private double _tokensPerSecond;
    [ObservableProperty] private int _usedSlotsCount;
    [ObservableProperty] private string _status = "Connecting...";
    [ObservableProperty] private string _statusColor = "#888888";

    public MetricsViewModel(string? apiBaseUrl)
    {
        _apiBaseUrl = apiBaseUrl;
        _metricsService = new MetricsService(new HttpClient { Timeout = TimeSpan.FromSeconds(2) });
        _metricsService.SetApiBaseUrl(apiBaseUrl);

        _ = PollMetricsAsync();
    }

     private async Task PollMetricsAsync()
    {
        _isRunning = true;
        while (_isRunning)
        {
            // Re-detect the llama-server URL on each poll via llama-swap /running.
            // The /running endpoint returns the upstream "proxy" field with the
            // llama-server address — no port scanning needed.
            var baseUrl = _apiBaseUrl;
            if (string.IsNullOrEmpty(baseUrl))
            {
                baseUrl = await DetectLlamaServerBaseUrlAsync();
                if (baseUrl is not null)
                {
                    _metricsService.SetApiBaseUrl(baseUrl);
                }
            }

            var metrics = await _metricsService.GetMetricsAsync();
            if (metrics != null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    PromptTokens = metrics.PromptTokens;
                    EvalTokens = metrics.EvalTokens;
                    TokensPerSecond = metrics.TokensPerSecond;
                    Status = "Connected";
                    StatusColor = "#A6E3A1";
                });
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    Status = "Disconnected or API unreachable";
                    StatusColor = "#F38BA8";
                });
            }

            await Task.Delay(1000);
        }
    }

    private static async Task<string?> DetectLlamaServerBaseUrlAsync()
    {
        // Primary: query llama-swap /running for the upstream proxy URL
        for (var port = 8080; port <= 8090; port++)
        {
            var swapUrl = $"http://127.0.0.1:{port}";
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var response = await http.GetAsync($"{swapUrl}/running");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var proxyMatch = System.Text.RegularExpressions.Regex.Match(
                        json,
                        "\"proxy\"\\s*:\\s*\"([^\"]+)\"");
                    if (proxyMatch.Success)
                    {
                        var proxyUrl = proxyMatch.Groups[1].Value;
                        proxyUrl = proxyUrl.Replace("localhost", "127.0.0.1");
                        return proxyUrl;
                    }
                }
            }
            catch { }
        }

        // Fallback: port scan llama-server 5800-5900
        for (var port = 5800; port <= 5900; port++)
        {
            var baseUrl = $"http://127.0.0.1:{port}";
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                var response = await http.GetAsync($"{baseUrl}/health");
                if (response.IsSuccessStatusCode)
                    return baseUrl;
            }
            catch { }
        }
        return null;
    }

    public void Dispose()
    {
        _isRunning = false;
    }
}
