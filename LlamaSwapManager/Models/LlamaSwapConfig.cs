using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace LlamaSwapManager.Models;

/// <summary>
/// Represents the full llama-swap configuration file structure.
/// </summary>
public class LlamaSwapConfig
{
    [YamlMember(Alias = "healthCheckTimeout")]
    public int? HealthCheckTimeout { get; set; }

    [YamlMember(Alias = "logLevel")]
    public string? LogLevel { get; set; } = "info";

    [YamlMember(Alias = "logTimeFormat")]
    public string? LogTimeFormat { get; set; }

    [YamlMember(Alias = "logToStdout")]
    public string? LogToStdout { get; set; } = "proxy";

    [YamlMember(Alias = "logFile")]
    public string? LogFile { get; set; }

    [YamlMember(Alias = "metricsMaxInMemory")]
    public int? MetricsMaxInMemory { get; set; }

    [YamlMember(Alias = "captureBuffer")]
    public int? CaptureBuffer { get; set; }

    [YamlMember(Alias = "performance")]
    public PerformanceConfig? Performance { get; set; }

    [YamlMember(Alias = "startPort")]
    public int? StartPort { get; set; } = 5800;

    [YamlMember(Alias = "sendLoadingState")]
    public bool? SendLoadingState { get; set; }

    [YamlMember(Alias = "includeAliasesInList")]
    public bool? IncludeAliasesInList { get; set; }

    [YamlMember(Alias = "globalTTL")]
    public int? GlobalTTL { get; set; }

    [YamlMember(Alias = "macros")]
    public Dictionary<string, object>? Macros { get; set; }

    [YamlMember(Alias = "apiKeys")]
    public List<string>? ApiKeys { get; set; }

    [YamlMember(Alias = "models")]
    public Dictionary<string, ModelConfig>? Models { get; set; } = new();

    [YamlMember(Alias = "matrix")]
    public MatrixConfig? Matrix { get; set; }

    [YamlMember(Alias = "hooks")]
    public HooksConfig? Hooks { get; set; }

    [YamlMember(Alias = "peers")]
    public Dictionary<string, PeerConfig>? Peers { get; set; }

    [YamlMember(Alias = "autoUpdate")]
    public AutoUpdateConfig? AutoUpdate { get; set; }

    [YamlMember(Alias = "binaries")]
    public Dictionary<string, BinaryConfig>? Binaries { get; set; }

    public string ToYaml()
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(YamlDotNet.Serialization.DefaultValuesHandling.OmitNull)
            .Build();
        return serializer.Serialize(this);
    }

    public static LlamaSwapConfig FromYaml(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .Build();
        return deserializer.Deserialize<LlamaSwapConfig>(yaml) ?? new LlamaSwapConfig();
    }
}

public class PerformanceConfig
{
    [YamlMember(Alias = "disabled")]
    public bool? Disabled { get; set; }

    [YamlMember(Alias = "every")]
    public string? Every { get; set; } = "5s";
}

public class ModelConfig
{
    [YamlMember(Alias = "cmd")]
    public string? Cmd { get; set; }

    [YamlMember(Alias = "cmdStop")]
    public string? CmdStop { get; set; }

    [YamlMember(Alias = "proxy")]
    public string? Proxy { get; set; }

    [YamlMember(Alias = "checkEndpoint")]
    public string? CheckEndpoint { get; set; } = "/health";

    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    [YamlMember(Alias = "env")]
    public List<string>? Env { get; set; }

    [YamlMember(Alias = "ttl")]
    public int? Ttl { get; set; }

    [YamlMember(Alias = "useModelName")]
    public string? UseModelName { get; set; }

    [YamlMember(Alias = "metrics")]
    public bool? Metrics { get; set; }

    [YamlMember(Alias = "slots")]
    public bool? Slots { get; set; }

    [YamlMember(Alias = "aliases")]
    public List<string>? Aliases { get; set; }

    [YamlMember(Alias = "unlisted")]
    public bool? Unlisted { get; set; }

    [YamlMember(Alias = "concurrencyLimit")]
    public int? ConcurrencyLimit { get; set; }

    [YamlMember(Alias = "sendLoadingState")]
    public bool? SendLoadingState { get; set; }

    [YamlMember(Alias = "macros")]
    public Dictionary<string, object>? Macros { get; set; }

    [YamlMember(Alias = "metadata")]
    public Dictionary<string, object>? Metadata { get; set; }

    [YamlMember(Alias = "filters")]
    public ModelFilters? Filters { get; set; }

    [YamlMember(Alias = "timeouts")]
    public ModelTimeouts? Timeouts { get; set; }
}

public class ModelFilters
{
    [YamlMember(Alias = "stripParams")]
    public string? StripParams { get; set; }

    [YamlMember(Alias = "setParams")]
    public Dictionary<string, object>? SetParams { get; set; }

    [YamlMember(Alias = "setParamsByID")]
    public Dictionary<string, object>? SetParamsById { get; set; }
}

public class ModelTimeouts
{
    [YamlMember(Alias = "connect")]
    public int? Connect { get; set; } = 30;

    [YamlMember(Alias = "keepalive")]
    public int? Keepalive { get; set; } = 30;

    [YamlMember(Alias = "responseHeader")]
    public int? ResponseHeader { get; set; } = 0;

    [YamlMember(Alias = "tlsHandshake")]
    public int? TlsHandshake { get; set; } = 10;

    [YamlMember(Alias = "idleConn")]
    public int? IdleConn { get; set; } = 90;
}

public class MatrixConfig
{
    [YamlMember(Alias = "vars")]
    public Dictionary<string, string>? Vars { get; set; } = new();

    [YamlMember(Alias = "evict_costs", ApplyNamingConventions = false)]
    public Dictionary<string, int>? EvictCosts { get; set; } = new();

    [YamlMember(Alias = "sets")]
    public Dictionary<string, string>? Sets { get; set; } = new();
}

public class HooksConfig
{
    [YamlMember(Alias = "on_startup", ApplyNamingConventions = false)]
    public StartupHooks? OnStartup { get; set; }
}

public class StartupHooks
{
    [YamlMember(Alias = "preload")]
    public List<string>? Preload { get; set; }
}

public class PeerConfig
{
    [YamlMember(Alias = "proxy")]
    public string? Proxy { get; set; }

    [YamlMember(Alias = "models")]
    public List<string>? Models { get; set; }

    [YamlMember(Alias = "apiKey")]
    public string? ApiKey { get; set; }
}

/// <summary>
/// Auto-update configuration for llama-swap and llama.cpp binaries.
/// </summary>
public class AutoUpdateConfig
{
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;

    [YamlMember(Alias = "checkOnStartup")]
    public bool CheckOnStartup { get; set; } = true;

    [YamlMember(Alias = "checkInterval")]
    public string? CheckInterval { get; set; } = "daily";

    [YamlMember(Alias = "autoDownload")]
    public bool AutoDownload { get; set; } = false;

    /// <summary>
    /// Forced CUDA toolkit version for llama.cpp downloads. Null or empty = auto-detect.
    /// </summary>
    [YamlMember(Alias = "cudaVersion")]
    public string? CudaVersion { get; set; }
}

/// <summary>
/// Per-binary auto-update settings.
/// </summary>
public class BinaryConfig
{
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;

    [YamlMember(Alias = "version")]
    public string? Version { get; set; }
}
