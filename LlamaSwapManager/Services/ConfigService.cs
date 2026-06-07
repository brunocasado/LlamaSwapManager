using System;
using System.Collections.Generic;
using System.IO;
using LlamaSwapManager.Models;
using YamlDotNet.Serialization;

namespace LlamaSwapManager.Services;

public class ConfigService
{
    private readonly string _configPath;
    private LlamaSwapConfig _config;
    private bool _hasChanges;

    public event Action<LlamaSwapConfig>? ConfigLoaded;
    public event Action? ConfigChanged;
    public event Action<string>? LogMessage;

    public bool HasChanges => _hasChanges;

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

    public void LoadConfig()
    {
        try
        {
            var yaml = File.ReadAllText(_configPath);
            _config = LlamaSwapConfig.FromYaml(yaml);
            _hasChanges = false;
            LogMessage?.Invoke($"Config loaded from {_configPath}");
            ConfigLoaded?.Invoke(_config);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Error loading config: {ex.Message}");
            _config = new LlamaSwapConfig();
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
            _hasChanges = false;
        }
        try
        {
            var yaml = _config.ToYaml();
            File.WriteAllText(targetPath, yaml);
            _hasChanges = false;
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
            _hasChanges = false;
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
        _hasChanges = true;
        ConfigChanged?.Invoke();
    }

    public void AddModel(string modelId, string name, string cmd, string? proxy = null)
    {
        _config.Models ??= new Dictionary<string, ModelConfig>();
        _config.Models[modelId] = new ModelConfig
        {
            Name = name,
            Cmd = cmd,
            Proxy = proxy,
            CheckEndpoint = "/health"
        };
        _hasChanges = true;
        ConfigChanged?.Invoke();
    }

    public void RemoveModel(string modelId)
    {
        if (_config.Models?.Remove(modelId) == true)
        {
            _hasChanges = true;
            ConfigChanged?.Invoke();
        }
    }

    public void UpdateModel(string modelId, Action<ModelConfig> updater)
    {
        if (_config.Models?.TryGetValue(modelId, out var model) == true)
        {
            updater(model);
            _hasChanges = true;
            ConfigChanged?.Invoke();
        }
    }

    public void SetMatrixVars(Dictionary<string, string> vars)
    {
        _config.Matrix ??= new MatrixConfig();
        _config.Matrix.Vars = vars;
        _hasChanges = true;
        ConfigChanged?.Invoke();
    }

    public void SetMatrixSets(Dictionary<string, string> sets)
    {
        _config.Matrix ??= new MatrixConfig();
        _config.Matrix.Sets = sets;
        _hasChanges = true;
        ConfigChanged?.Invoke();
    }

    public void SetMatrixEvictCosts(Dictionary<string, int> costs)
    {
        _config.Matrix ??= new MatrixConfig();
        _config.Matrix.EvictCosts = costs;
        _hasChanges = true;
        ConfigChanged?.Invoke();
    }

    public void ClearMatrix()
    {
        _config.Matrix = null;
        _hasChanges = true;
        ConfigChanged?.Invoke();
    }

    public void AddRuntime(string runtimeId, string name, string path, string defaultArgs)
    {
        _config.Macros ??= new Dictionary<string, object>();
        _config.Macros[$"{runtimeId}-path"] = path;
        _config.Macros[$"{runtimeId}-args"] = defaultArgs;
        _config.Macros[$"{runtimeId}-name"] = name;
        _hasChanges = true;
        ConfigChanged?.Invoke();
    }

    public void RemoveRuntime(string runtimeId)
    {
        if (_config.Macros is null) return;
        _config.Macros.Remove($"{runtimeId}-path");
        _config.Macros.Remove($"{runtimeId}-args");
        _config.Macros.Remove($"{runtimeId}-name");
        _hasChanges = true;
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
}
