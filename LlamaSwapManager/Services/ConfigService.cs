using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LlamaSwapManager.Models;
using YamlDotNet.Serialization;

namespace LlamaSwapManager.Services;

/// <summary>
/// Service for loading, saving, and tracking changes to llama-swap config.yml files.
/// Maintains a snapshot of the last-saved config for diff tracking.
/// </summary>
public class ConfigService
{
    private readonly string _configPath;
    private LlamaSwapConfig _config;
    private string? _lastSaved;
    private readonly HashSet<string> _dirtyFields = new();

    public event Action<LlamaSwapConfig>? ConfigLoaded;
    public event Action? ConfigChanged;
    public event Action<string>? LogMessage;

    public bool HasChanges => _dirtyFields.Count > 0;

    public ConfigService(string? configPath = null)
    {
        _configPath = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".llama-swap", "config.yml");
        _config = new LlamaSwapConfig();

        if (File.Exists(_configPath))
            LoadConfig();
    }

    public string ConfigPath => _configPath;

    /// <summary>
    /// List of field paths that differ from the last saved state.
    /// Empty if no changes or never saved.
    /// </summary>
    public IReadOnlyCollection<string> GetDirtyFields()
    {
        return _dirtyFields.ToList().AsReadOnly();
    }

    /// <summary>
    /// Get a snapshot of the current config as YAML.
    /// </summary>
    public string GetCurrentSnapshot()
    {
        return _config.ToYaml();
    }

    /// <summary>
    /// Compare the current config against the last saved snapshot.
    /// Returns a list of changed field paths.
    /// </summary>
    public List<string> CompareConfigs()
    {
        if (_lastSaved == null)
            return new List<string>();

        var currentYaml = _config.ToYaml();
        var differences = new List<string>();

        // Compare top-level fields
        var current = ParseYamlToDict(currentYaml);
        var saved = ParseYamlToDict(_lastSaved);

        CompareDictionaries(current, saved, "", differences);

        return differences;
    }

    /// <summary>
    /// Validate the current config using the 3-layer validator.
    /// Returns (isValid, errors) tuple.
    /// </summary>
    public (bool IsValid, List<string> Errors) ValidateConfig()
    {
        return ConfigValidator.Validate(_config);
    }

    /// <summary>
    /// Validate a config without loading it into the service.
    /// </summary>
    public static (bool IsValid, List<string> Errors) ValidateConfig(LlamaSwapConfig config)
    {
        return ConfigValidator.Validate(config);
    }

    /// <summary>
    /// Save the current config to disk and update the snapshot.
    /// Returns (success, errors) tuple.
    /// </summary>
    public (bool Success, List<string> Errors) SaveConfigWithValidation(string? path = null)
    {
        var validation = ValidateConfig();

        if (!validation.IsValid)
        {
            LogMessage?.Invoke($"Validation failed with {validation.Errors.Count} error(s):");
            foreach (var error in validation.Errors)
                LogMessage?.Invoke($"  - {error}");
            return (false, validation.Errors);
        }

        try
        {
            var targetPath = path ?? _configPath;
            var yaml = _config.ToYaml();
            File.WriteAllText(targetPath, yaml);
            _lastSaved = yaml;
            _dirtyFields.Clear();
            LogMessage?.Invoke($"Config saved to {targetPath}");
            return (true, new List<string>());
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Error saving config: {ex.Message}");
            return (false, new List<string> { ex.Message });
        }
    }

    public void LoadConfig()
    {
        try
        {
            var yaml = File.ReadAllText(_configPath);
            _config = LlamaSwapConfig.FromYaml(yaml);
            _lastSaved = yaml;
            _dirtyFields.Clear();
            LogMessage?.Invoke($"Config loaded from {_configPath}");
            ConfigLoaded?.Invoke(_config);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Error loading config: {ex.Message}");
            _config = new LlamaSwapConfig();
            _lastSaved = null;
            _dirtyFields.Clear();
        }
    }

    public static LlamaSwapConfig? LoadConfig(string? configPath)
    {
        if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
            return null;

        try
        {
            var yaml = File.ReadAllText(configPath);
            return LlamaSwapConfig.FromYaml(yaml);
        }
        catch
        {
            return null;
        }
    }

    public static string Serialize(LlamaSwapConfig? config)
    {
        if (config is null) return "";
        return config.ToYaml();
    }

    public void SaveConfig(LlamaSwapConfig? config, string? path = null)
    {
        var targetPath = path ?? _configPath;
        if (config is not null)
        {
            _config = config;
            _dirtyFields.Clear();
        }
        try
        {
            var yaml = _config.ToYaml();
            File.WriteAllText(targetPath, yaml);
            _lastSaved = yaml;
            _dirtyFields.Clear();
            LogMessage?.Invoke($"Config saved to {targetPath}");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Error saving config: {ex.Message}");
        }
    }

    public void SaveConfig()
    {
        try
        {
            var yaml = _config.ToYaml();
            File.WriteAllText(_configPath, yaml);
            _lastSaved = yaml;
            _dirtyFields.Clear();
            LogMessage?.Invoke($"Config saved to {_configPath}");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Error saving config: {ex.Message}");
        }
    }

    public LlamaSwapConfig Config => _config;

    public void SetConfig(LlamaSwapConfig config)
    {
        _config = config;
        TrackAllChanges();
        ConfigChanged?.Invoke();
    }

    /// <summary>
    /// Add a new model with a configurable CheckEndpoint.
    /// </summary>
    public void AddModel(string modelId, string name, string cmd, string? proxy = null, string? checkEndpoint = "/health")
    {
        _config.Models ??= new Dictionary<string, ModelConfig>();
        _config.Models[modelId] = new ModelConfig
        {
            Name = name,
            Cmd = cmd,
            Proxy = proxy,
            CheckEndpoint = string.IsNullOrWhiteSpace(checkEndpoint) || checkEndpoint == "none" ? checkEndpoint : "/health"
        };
        TrackModelChange(modelId);
        ConfigChanged?.Invoke();
    }

    public void RemoveModel(string modelId)
    {
        if (_config.Models?.Remove(modelId) == true)
        {
            TrackModelChange(modelId);
            ConfigChanged?.Invoke();
        }
    }

    public void UpdateModel(string modelId, Action<ModelConfig> updater)
    {
        if (_config.Models?.TryGetValue(modelId, out var model) == true)
        {
            updater(model);
            TrackModelChange(modelId);
            ConfigChanged?.Invoke();
        }
    }

    public void SetMatrixVars(Dictionary<string, string> vars)
    {
        _config.Matrix ??= new MatrixConfig();
        _config.Matrix.Vars = vars;
        TrackMatrixChange("vars");
        ConfigChanged?.Invoke();
    }

    public void SetMatrixSets(Dictionary<string, string> sets)
    {
        _config.Matrix ??= new MatrixConfig();
        _config.Matrix.Sets = sets;
        TrackMatrixChange("sets");
        ConfigChanged?.Invoke();
    }

    public void SetMatrixEvictCosts(Dictionary<string, int> costs)
    {
        _config.Matrix ??= new MatrixConfig();
        _config.Matrix.EvictCosts = costs;
        TrackMatrixChange("evict_costs");
        ConfigChanged?.Invoke();
    }

    public void ClearMatrix()
    {
        _config.Matrix = null;
        _dirtyFields.Add("matrix");
        ConfigChanged?.Invoke();
    }

    public void AddRuntime(string runtimeId, string name, string path, string defaultArgs)
    {
        _config.Macros ??= new Dictionary<string, object>();
        _config.Macros[$"{runtimeId}-path"] = path;
        _config.Macros[$"{runtimeId}-args"] = defaultArgs;
        _config.Macros[$"{runtimeId}-name"] = name;
        TrackMacroChange(runtimeId);
        ConfigChanged?.Invoke();
    }

    public void RemoveRuntime(string runtimeId)
    {
        if (_config.Macros is null) return;
        _config.Macros.Remove($"{runtimeId}-path");
        _config.Macros.Remove($"{runtimeId}-args");
        _config.Macros.Remove($"{runtimeId}-name");
        TrackMacroChange(runtimeId);
        ConfigChanged?.Invoke();
    }

    public Dictionary<string, string> GetRuntimes()
    {
        var runtimes = new Dictionary<string, string>();
        if (_config.Macros is null) return runtimes;

        var runtimeNames = new HashSet<string>();
        foreach (var key in _config.Macros.Keys)
        {
            if (key.EndsWith("-name"))
                runtimeNames.Add(key.Substring(0, key.Length - 5));
        }

        foreach (var runtimeId in runtimeNames)
        {
            var name = _config.Macros[$"{runtimeId}-name"]?.ToString() ?? runtimeId;
            runtimes[runtimeId] = name;
        }

        return runtimes;
    }

    /// <summary>
    /// Update a top-level config field and track the change.
    /// </summary>
    public void UpdateTopLevelField(string fieldName, object? value)
    {
        switch (fieldName)
        {
            case "healthCheckTimeout":
                _config.HealthCheckTimeout = value as int?;
                break;
            case "logLevel":
                _config.LogLevel = value as string;
                break;
            case "logTimeFormat":
                _config.LogTimeFormat = value as string;
                break;
            case "logToStdout":
                _config.LogToStdout = value as string;
                break;
            case "metricsMaxInMemory":
                _config.MetricsMaxInMemory = value as int?;
                break;
            case "captureBuffer":
                _config.CaptureBuffer = value as int?;
                break;
            case "startPort":
                _config.StartPort = value as int?;
                break;
            case "sendLoadingState":
                _config.SendLoadingState = value as bool?;
                break;
            case "includeAliasesInList":
                _config.IncludeAliasesInList = value as bool?;
                break;
            case "globalTTL":
                _config.GlobalTTL = value as int?;
                break;
            default:
                return;
        }
        _dirtyFields.Add(fieldName);
        ConfigChanged?.Invoke();
    }

    // --- Internal tracking methods ---

    private void TrackModelChange(string modelId)
    {
        _dirtyFields.Add($"models.{modelId}");
    }

    private void TrackMatrixChange(string field)
    {
        _dirtyFields.Add($"matrix.{field}");
    }

    private void TrackMacroChange(string runtimeId)
    {
        _dirtyFields.Add($"macros.{runtimeId}");
    }

    private void TrackAllChanges()
    {
        _dirtyFields.Clear();
        if (_config.Models != null)
        {
            foreach (var modelId in _config.Models.Keys)
            {
                _dirtyFields.Add($"models.{modelId}");
            }
        }
        if (_config.Matrix != null)
        {
            if (_config.Matrix.Vars != null) _dirtyFields.Add("matrix.vars");
            if (_config.Matrix.Sets != null) _dirtyFields.Add("matrix.sets");
            if (_config.Matrix.EvictCosts != null) _dirtyFields.Add("matrix.evict_costs");
        }
        if (_config.Macros != null)
        {
            foreach (var key in _config.Macros.Keys)
            {
                if (key.EndsWith("-name"))
                {
                    var runtimeId = key.Substring(0, key.Length - 5);
                    _dirtyFields.Add($"macros.{runtimeId}");
                }
            }
        }
    }

    // --- Dictionary comparison for diff ---

    private static Dictionary<string, object?> ParseYamlToDict(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .Build();

        var result = new Dictionary<string, object?>();
        var lines = yaml.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;

            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx < 0) continue;

            var key = trimmed[..colonIdx].Trim();
            var value = trimmed[(colonIdx + 1)..].Trim();

            if (!string.IsNullOrEmpty(key))
            {
                result[key] = string.IsNullOrEmpty(value) ? null : value;
            }
        }
        return result;
    }

    private static void CompareDictionaries(Dictionary<string, object?> current, Dictionary<string, object?> saved, string prefix, List<string> differences)
    {
        foreach (var kvp in current)
        {
            var path = string.IsNullOrEmpty(prefix) ? kvp.Key : $"{prefix}.{kvp.Key}";

            if (!saved.TryGetValue(kvp.Key, out var savedValue))
            {
                differences.Add(path);
                continue;
            }

            var currentStr = kvp.Value?.ToString() ?? "";
            var savedStr = savedValue?.ToString() ?? "";

            if (currentStr != savedStr)
            {
                differences.Add(path);
            }
        }
    }
}
