using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LlamaSwapManager.Models;
using YamlDotNet.Serialization;

namespace LlamaSwapManager.Services;

/// <summary>
/// Service for loading, saving, and tracking changes to llama-swap config.yml files.
/// Maintains a snapshot of the last-saved config for diff tracking.
/// M1: API keys are encrypted before writing to disk.
/// </summary>
public class ConfigService
{
    private readonly string _configPath;
    private LlamaSwapConfig _config;
    private string? _lastSaved;
    private readonly HashSet<string> _dirtyFields = new();

    // M1: Simple encryption key for API keys (derived from app-specific entropy)
    private static readonly byte[] _encryptionKey = Encoding.UTF8.GetBytes("LlamaSwapMgr!@#$%^&*()_+2024sec");

    // M1: Regex pattern for API key fields — uses \x22 for double quote to avoid verbatim string issues
    private static readonly string ApiKeyPattern = @"^(\s*)(apiKey|api_key|apiKeyValue|token|secret|authToken):\s*(?:\x22|')?([^\x22\x27#\r\n]+)(?:\x22|')?\s*(#.*)?$";

    public event Action<LlamaSwapConfig>? ConfigLoaded;
    public event Action? ConfigChanged;
    public event Action<string>? LogMessage;

    public bool HasChanges => _dirtyFields.Count > 0;

    public ConfigService(string? configPath = null)
    {
        _configPath = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LlamaSwapManager");
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
            // M1: Encrypt API keys before writing
            yaml = EncryptYamlApiKeys(yaml);
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
            // M1: Decrypt API keys after loading
            yaml = DecryptYamlApiKeys(yaml);
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
            // M1: Encrypt API keys before writing
            yaml = EncryptYamlApiKeys(yaml);
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
            // M1: Encrypt any plaintext API keys in the YAML before saving
            yaml = EncryptYamlApiKeys(yaml);
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

    /// <summary>
    /// Ensure auto-update config has defaults (migration for old configs).
    /// </summary>
    public void EnsureAutoUpdateDefaults()
    {
        if (_config.AutoUpdate == null)
        {
            _config.AutoUpdate = new AutoUpdateConfig();
            TrackAutoUpdateChange();
        }
        else
        {
            // Apply defaults for missing fields in old configs
            if (_config.AutoUpdate.CheckInterval == null)
            {
                _config.AutoUpdate.CheckInterval = "daily";
                TrackAutoUpdateChange();
            }
        }
    }

    /// <summary>
    /// Set the auto-update enabled flag.
    /// </summary>
    public void SetAutoUpdateEnabled(bool enabled)
    {
        EnsureAutoUpdateDefaults();
        _config.AutoUpdate!.Enabled = enabled;
        TrackAutoUpdateChange();
        ConfigChanged?.Invoke();
    }

    /// <summary>
    /// Set the check-on-startup flag.
    /// </summary>
    public void SetCheckOnStartup(bool checkOnStartup)
    {
        EnsureAutoUpdateDefaults();
        _config.AutoUpdate!.CheckOnStartup = checkOnStartup;
        TrackAutoUpdateChange();
        ConfigChanged?.Invoke();
    }

    /// <summary>
    /// Set the check interval (daily, weekly, monthly, manual).
    /// </summary>
    public void SetCheckInterval(string interval)
    {
        EnsureAutoUpdateDefaults();
        _config.AutoUpdate!.CheckInterval = interval;
        TrackAutoUpdateChange();
        ConfigChanged?.Invoke();
    }

    /// <summary>
    /// Set the auto-download flag.
    /// </summary>
    public void SetAutoDownload(bool autoDownload)
    {
        EnsureAutoUpdateDefaults();
        _config.AutoUpdate!.AutoDownload = autoDownload;
        TrackAutoUpdateChange();
        ConfigChanged?.Invoke();
    }

    /// <summary>
    /// Set binary-specific auto-update settings.
    /// </summary>
    public void SetBinaryConfig(string binaryName, bool enabled, string? version = null)
    {
        _config.Binaries ??= new Dictionary<string, BinaryConfig>();
        if (!_config.Binaries.TryGetValue(binaryName, out var binaryConfig))
        {
            binaryConfig = new BinaryConfig();
            _config.Binaries[binaryName] = binaryConfig;
        }
        binaryConfig.Enabled = enabled;
        if (version != null)
        {
            binaryConfig.Version = version;
        }
        TrackBinaryChange(binaryName);
        ConfigChanged?.Invoke();
    }

    /// <summary>
    /// Get the current auto-update config, ensuring defaults exist.
    /// </summary>
    public AutoUpdateConfig GetAutoUpdateConfig()
    {
        EnsureAutoUpdateDefaults();
        return _config.AutoUpdate!;
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
            case "autoUpdateEnabled":
                SetAutoUpdateEnabled(value as bool? == true);
                return;
            case "autoUpdateCheckOnStartup":
                SetCheckOnStartup(value as bool? == true);
                return;
            case "autoUpdateCheckInterval":
                if (value is string interval) SetCheckInterval(interval);
                return;
            case "autoUpdateAutoDownload":
                SetAutoDownload(value as bool? == true);
                return;
            case "binaries":
                if (value is Dictionary<string, BinaryConfig> binaries)
                {
                    _config.Binaries = binaries;
                    TrackAllChanges();
                    ConfigChanged?.Invoke();
                }
                return;
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

    private void TrackAutoUpdateChange()
    {
        _dirtyFields.Add("autoUpdate");
    }

    private void TrackBinaryChange(string binaryName)
    {
        _dirtyFields.Add($"binaries.{binaryName}");
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
        if (_config.AutoUpdate != null)
        {
            _dirtyFields.Add("autoUpdate");
        }
        if (_config.Binaries != null)
        {
            foreach (var key in _config.Binaries.Keys)
            {
                _dirtyFields.Add($"binaries.{key}");
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

    // =====================================================================
    // M1: API key encryption/decryption
    // =====================================================================

    /// <summary>
    /// Encrypts a plaintext string using AES-256-CBC.
    /// Returns base64-encoded ciphertext.
    /// </summary>
    private static string EncryptApiKey(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = _encryptionKey.Take(32).ToArray();
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Prepend IV to ciphertext
        var combined = new byte[aes.IV!.Length + cipherBytes.Length];
        Array.Copy(aes.IV, 0, combined, 0, aes.IV.Length);
        Array.Copy(cipherBytes, 0, combined, aes.IV.Length, cipherBytes.Length);

        return Convert.ToBase64String(combined);
    }

    /// <summary>
    /// Decrypts a base64-encoded AES-256-CBC ciphertext.
    /// Returns plaintext string, or null if decryption fails.
    /// </summary>
    private static string? DecryptApiKey(string ciphertext)
    {
        try
        {
            using var aes = Aes.Create();
            aes.Key = _encryptionKey.Take(32).ToArray();

            var combined = Convert.FromBase64String(ciphertext);
            if (combined.Length < aes.IV!.Length) return null;

            aes.IV = combined.Take(aes.IV.Length).ToArray();
            var cipherBytes = combined.Skip(aes.IV.Length).ToArray();

            using var decryptor = aes.CreateDecryptor();
            var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if a string looks like an encrypted API key (base64-encoded AES ciphertext).
    /// </summary>
    private static bool IsEncrypted(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        // Encrypted values are base64 and typically start with a letter, contain padding
        return value.Length > 16 && value.All(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                       (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=');
    }

    /// <summary>
    /// M1: Post-process YAML to encrypt API key values.
    /// Looks for common API key field patterns and encrypts their values.
    /// </summary>
    private static string EncryptYamlApiKeys(string yaml)
    {
        var lines = yaml.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var match = System.Text.RegularExpressions.Regex.Match(line, ApiKeyPattern);
            if (match.Success)
            {
                var value = match.Groups[3].Value.Trim();
                // Only encrypt if it looks like a real key (not empty, not a path, not encrypted)
                if (!string.IsNullOrEmpty(value) && value.Length > 3 && !value.Contains('/') && !IsEncrypted(value))
                {
                    var encrypted = EncryptApiKey(value);
                    var prefix = match.Groups[1].Value;
                    var keyName = match.Groups[2].Value;
                    var comment = match.Groups[4].Value;
                    lines[i] = $"{prefix}{keyName}: \"{encrypted}\"{comment}";
                }
            }
        }
        return string.Join('\n', lines);
    }

    /// <summary>
    /// M1: Post-process YAML to decrypt API key values.
    /// Looks for common API key field patterns and decrypts encrypted values.
    /// </summary>
    private static string DecryptYamlApiKeys(string yaml)
    {
        var lines = yaml.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var match = System.Text.RegularExpressions.Regex.Match(line, ApiKeyPattern);
            if (match.Success)
            {
                var value = match.Groups[3].Value.Trim().Trim('"', '\'');
                if (IsEncrypted(value))
                {
                    var decrypted = DecryptApiKey(value);
                    if (decrypted != null)
                    {
                        var prefix = match.Groups[1].Value;
                        var keyName = match.Groups[2].Value;
                        var comment = match.Groups[4].Value;
                        lines[i] = $"{prefix}{keyName}: \"{decrypted}\"{comment}";
                    }
                }
            }
        }
        return string.Join('\n', lines);
    }
}