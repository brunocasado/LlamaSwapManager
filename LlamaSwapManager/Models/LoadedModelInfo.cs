using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LlamaSwapManager.Models;

public class LoadedModelInfo
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("proxy")]
    public string Proxy { get; set; } = string.Empty;

    public string DisplayName => Name ?? Model;
    public bool IsReady => State == "ready";
}

public class RunningResponse
{
    [JsonPropertyName("running")]
    public List<LoadedModelInfo> Running { get; set; } = new();
}
