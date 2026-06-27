using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

/// <summary>
/// Streams logs from llama-swap's /logs/stream/upstream SSE endpoint.
/// Single persistent stream with automatic reconnect on disconnect.
/// Batches received lines to reduce UI dispatcher pressure.
/// </summary>
public class LogStreamService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _apiBaseUrl;
    private readonly ConcurrentQueue<string> _logBuffer = new();

    /// <summary>
    /// How long to wait before flushing a batch of accumulated lines.
    /// Lower = more responsive, higher = less UI pressure.
    /// </summary>
    private const int BatchIntervalMs = 100;

    private Task? _streamTask;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public event Action<string>? LogReceived;
    public event Action<IReadOnlyList<string>>? LogBatchReceived;
    public bool IsRunning => _isRunning;

    public LogStreamService(HttpClient httpClient, string apiBaseUrl)
    {
        _httpClient = httpClient;
        _apiBaseUrl = apiBaseUrl;
    }

    /// <summary>
    /// Starts streaming upstream logs via SSE.
    /// </summary>
    public async Task StartAsync(string _modelId, CancellationToken ct = default)
    {
        // Clean up any previous stream state before starting fresh.
        if (_streamTask is not null || _cts is not null)
            await StopAsync();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isRunning = true;
        _streamTask = Task.Run(() => StreamLogsAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _isRunning = false;
        _cts?.Cancel();
        if (_streamTask is not null)
        {
            var doneTask = await Task.WhenAny(_streamTask, Task.Delay(2000));
            if (doneTask == _streamTask)
            {
                try { await _streamTask; } catch { /* stream ended */ }
            }
        }
        _cts?.Dispose();
        _cts = null;
        _streamTask = null;
    }

    private async Task StreamLogsAsync(CancellationToken ct)
    {
        var url = $"{_apiBaseUrl}/logs/stream/upstream";

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

                // Batch accumulator — collect lines and flush periodically
                var batch = new List<string>(64);
                var lastFlush = DateTime.UtcNow;

                while (!ct.IsCancellationRequested && _isRunning)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null)
                        break; // Stream ended, reconnect

                    _logBuffer.Enqueue(line);
                    batch.Add(line);

                    // Flush batch on interval or when it gets large
                    var now = DateTime.UtcNow;
                    if (batch.Count >= 32 || (now - lastFlush).TotalMilliseconds >= BatchIntervalMs)
                    {
                        var snapshot = batch.ToArray();
                        batch.Clear();
                        lastFlush = now;

                        // Fire batched event (preferred) and legacy single-line event
                        LogBatchReceived?.Invoke(snapshot);
                        foreach (var batchLine in snapshot)
                            LogReceived?.Invoke(batchLine);
                    }
                }

                // Flush remaining lines before reconnect/exit
                if (batch.Count > 0)
                {
                    var snapshot = batch.ToArray();
                    batch.Clear();
                    LogBatchReceived?.Invoke(snapshot);
                    foreach (var batchLine in snapshot)
                        LogReceived?.Invoke(batchLine);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Reconnect after delay
                if (!ct.IsCancellationRequested && _isRunning)
                    await Task.Delay(3000, ct);
            }
        }
        _isRunning = false;
    }

    /// <summary>
    /// Gets all buffered log lines.
    /// </summary>
    public IReadOnlyList<string> GetLogs()
    {
        return _logBuffer.ToList();
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
