using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

/// <summary>
/// Streams logs from llama-swap's /logs/stream/upstream endpoint.
/// Returns real-time llama-server logs in the format:
///   {timestamp} {level} {component}: {message}
/// </summary>
public class LogStreamService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _apiBaseUrl;
    private readonly ConcurrentBag<string> _logBuffer = new();
    private readonly object _lock = new();
    private Task? _streamTask;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public event Action<string>? LogReceived;
    public bool IsRunning => _isRunning;

    public LogStreamService(HttpClient httpClient, string apiBaseUrl)
    {
        _httpClient = httpClient;
        _apiBaseUrl = apiBaseUrl;
    }

    /// <summary>
    /// Starts streaming upstream logs.
    /// </summary>
    public async Task StartAsync(string modelId, CancellationToken ct = default)
    {
        if (_isRunning)
            await StopAsync();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _streamTask = Task.Run(() => StreamLogsAsync(_cts.Token));
        _isRunning = true;
    }

    public async Task StopAsync()
    {
        _isRunning = false;
        _cts?.Cancel();
        if (_streamTask is not null)
            await _streamTask;
        _cts?.Dispose();
        _cts = null;
        _streamTask = null;
    }

    private async Task StreamLogsAsync(CancellationToken ct)
    {
        var url = $"{_apiBaseUrl}/logs/stream/upstream";

        try
        {
            // Fetch historical logs first (with timeout to get past logs)
            await FetchHistoricalLogsAsync(url, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // Historical fetch failed, continue with streaming
        }

        // Then stream new logs
        while (!ct.IsCancellationRequested && _isRunning)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    await Task.Delay(2000, ct);
                    continue;
                }

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);

                while (!ct.IsCancellationRequested && _isRunning)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null)
                        break; // Stream ended, reconnect

                    _logBuffer.Add(line);
                    LogReceived?.Invoke(line);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Reconnect after delay
                if (!ct.IsCancellationRequested)
                    await Task.Delay(3000, ct);
            }
        }
    }

    private async Task FetchHistoricalLogsAsync(string url, CancellationToken ct)
    {
        // Use a timeout to fetch historical logs, then stop reading
        // The stream will continue in the background, but we only want the initial burst
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10)); // 10 second timeout for historical logs

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            
            if (!response.IsSuccessStatusCode)
                return;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            while (!cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (line is null)
                    break;

                _logBuffer.Add(line);
                LogReceived?.Invoke(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: timeout after 10 seconds
        }
        catch (Exception)
        {
            // Fetch failed
        }
    }

    /// <summary>
    /// Gets all buffered log lines.
    /// </summary>
    public IReadOnlyList<string> GetLogs()
    {
        lock (_lock)
        {
            return new List<string>(_logBuffer);
        }
    }

    /// <summary>
    /// Clears all buffered logs.
    /// </summary>
    public void ClearLogs()
    {
        _logBuffer.Clear();
    }

    public void Dispose()
    {
        _isRunning = false;
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
