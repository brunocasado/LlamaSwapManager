using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

public class MetricsService
{
    private readonly HttpClient _httpClient;
    private string? _apiBaseUrl;

    public MetricsService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public void SetApiBaseUrl(string? url)
    {
        _apiBaseUrl = url;
    }

    public string? ApiBaseUrl => _apiBaseUrl;

    public async Task<MetricsData?> GetMetricsAsync()
    {
        if (string.IsNullOrEmpty(_apiBaseUrl)) return null;

        try
        {
            var response = await _httpClient.GetAsync(_apiBaseUrl + "/metrics");
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            return ParsePrometheusMetrics(content);
        }
        catch
        {
            return null;
        }
    }

    private static MetricsData ParsePrometheusMetrics(string content)
    {
        var data = new MetricsData();

        // llama-server prometheus format: llamacpp:<metric_name> <value>
        // Counters: prompt_tokens_total, tokens_predicted_total, n_decode_total
        // Gauges: requests_processing (active slots), predicted_tokens_seconds (tokens/sec)
        //
        // CRITICAL: Use [^\S\n]+ instead of \s+ to avoid matching across lines.
        // Prometheus metrics are one-per-line; \s+ includes \n which would
        // incorrectly span HELP/TYPE comment lines into the value.
        //
        // CRITICAL: Use InvariantCulture for double.TryParse — Prometheus always
        // uses '.' as decimal separator. In pt-BR culture, '.' is a thousands
        // separator, so "14.4605" would be parsed as 144605.0.
        foreach (Match match in Regex.Matches(content, @"llamacpp:(\w+)[^\S\n]+([\d.]+)"))
        {
            var name = match.Groups[1].Value;
            if (double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                data.MetricValues[name] = value;
            }
        }

        // prompt_tokens_total = prefill tokens
        if (data.MetricValues.TryGetValue("prompt_tokens_total", out var promptTokens))
            data.PromptTokens = promptTokens;

        // tokens_predicted_total = decode tokens
        if (data.MetricValues.TryGetValue("tokens_predicted_total", out var decodeTokens))
            data.EvalTokens = decodeTokens;

        // predicted_tokens_seconds = avg tokens/sec during generation (historical average)
        if (data.MetricValues.TryGetValue("predicted_tokens_seconds", out var tps))
            data.TokensPerSecond = tps;

        // requests_processing = active slots (gauge)
        if (data.MetricValues.TryGetValue("requests_processing", out var active))
            data.ActiveSlots = (int)active;

        return data;
    }
}

public class MetricsData
{
    public double PromptTokens { get; set; }
    public double EvalTokens { get; set; }
    public double TokensPerSecond { get; set; }
    public int ActiveSlots { get; set; }
    public Dictionary<string, double> MetricValues { get; } = new();
}
