using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LlamaSwapManager.Models;
using YamlDotNet.Serialization;

namespace LlamaSwapManager.Services;

/// <summary>
/// Validator for llama-swap config.yml in 3 layers:
/// 1. Structural — YAML parses, required fields present
/// 2. Semantic — business rules (endpoints, ports, values)
/// 3. Dry-run — round-trip serialization is valid
/// </summary>
public static class ConfigValidator
{
    // Reserved macro names that cannot be used
    private static readonly HashSet<string> ReservedMacros = new(StringComparer.OrdinalIgnoreCase)
    {
        "PORT", "MODEL_ID"
    };

    // Valid log levels
    private static readonly HashSet<string> ValidLogLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "debug", "info", "warn", "error"
    };

    // Valid logToStdout values
    private static readonly HashSet<string> ValidLogToStdout = new(StringComparer.OrdinalIgnoreCase)
    {
        "proxy", "upstream", "both", "none"
    };

    // Valid check interval values
    private static readonly HashSet<string> ValidCheckIntervals = new(StringComparer.OrdinalIgnoreCase)
    {
        "daily", "weekly", "monthly", "manual"
    };

    // Valid binary names
    private static readonly HashSet<string> ValidBinaryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "llamaSwap", "llamaCpp"
    };

    /// <summary>
    /// Full validation: structural + semantic + dry-run.
    /// Returns (isValid, errors) tuple.
    /// </summary>
    public static (bool IsValid, List<string> Errors) Validate(LlamaSwapConfig config)
    {
        var errors = new List<string>();

        // Layer 1: Structural
        var structuralErrors = ValidateStructural(config);
        errors.AddRange(structuralErrors);

        // Layer 2: Semantic (only if structural passes)
        if (!structuralErrors.Any())
        {
            var semanticErrors = ValidateSemantic(config);
            errors.AddRange(semanticErrors);
        }

        // Layer 3: Dry-run (round-trip serialization)
        var dryRunErrors = ValidateDryRun(config);
        errors.AddRange(dryRunErrors);

        return (errors.Count == 0, errors);
    }

    /// <summary>
    /// Structural validation: YAML parses, required fields present.
    /// </summary>
    public static List<string> ValidateStructural(LlamaSwapConfig config)
    {
        var errors = new List<string>();

        // Required: models must be present (can be empty dict, but not null)
        if (config.Models == null || config.Models.Count == 0)
        {
            errors.Add("Config must have at least one model defined under 'models'.");
        }

        // Validate each model has required fields
        if (config.Models != null)
        {
            foreach (var kvp in config.Models)
            {
                var modelId = kvp.Key;
                var model = kvp.Value;

                if (string.IsNullOrWhiteSpace(modelId))
                {
                    errors.Add("Model key (ID) cannot be empty.");
                }

                if (model == null)
                {
                    errors.Add($"Model '{modelId}' has a null configuration.");
                    continue;
                }

                // cmd or proxy is required
                if (string.IsNullOrWhiteSpace(model.Cmd) && string.IsNullOrWhiteSpace(model.Proxy))
                {
                    errors.Add($"Model '{modelId}': either 'cmd' or 'proxy' is required.");
                }

                // Validate macro names in top-level macros
                if (config.Macros != null)
                {
                    foreach (var macroName in config.Macros.Keys)
                    {
                        if (macroName.Length > 64)
                        {
                            errors.Add($"Macro '{macroName}' exceeds 64 character limit.");
                        }

                        if (!Regex.IsMatch(macroName, "^[a-zA-Z0-9_-]+$"))
                        {
                            errors.Add($"Macro '{macroName}' contains invalid characters. Only alphanumeric, underscore, and hyphen allowed.");
                        }

                        if (ReservedMacros.Contains(macroName))
                        {
                            errors.Add($"Macro '{macroName}' is a reserved name and cannot be used.");
                        }
                    }
                }
            }
        }

        // Validate logLevel if set
        if (!string.IsNullOrEmpty(config.LogLevel) && !ValidLogLevels.Contains(config.LogLevel))
        {
            errors.Add($"Invalid logLevel: '{config.LogLevel}'. Valid values: debug, info, warn, error.");
        }

        // Validate logToStdout if set
        if (!string.IsNullOrEmpty(config.LogToStdout) && !ValidLogToStdout.Contains(config.LogToStdout))
        {
            errors.Add($"Invalid logToStdout: '{config.LogToStdout}'. Valid values: proxy, upstream, both, none.");
        }

        return errors;
    }

    /// <summary>
    /// Semantic validation: business rules, value ranges, cross-field consistency.
    /// </summary>
    public static List<string> ValidateSemantic(LlamaSwapConfig config)
    {
        var errors = new List<string>();

        // healthCheckTimeout must be >= 15
        if (config.HealthCheckTimeout.HasValue && config.HealthCheckTimeout.Value < 15)
        {
            errors.Add($"healthCheckTimeout must be at least 15 seconds (got {config.HealthCheckTimeout}).");
        }

        // globalTTL must be >= 0
        if (config.GlobalTTL.HasValue && config.GlobalTTL.Value < 0)
        {
            errors.Add($"globalTTL cannot be negative (got {config.GlobalTTL}).");
        }

        // startPort must be positive
        if (config.StartPort.HasValue && config.StartPort.Value <= 0)
        {
            errors.Add($"startPort must be a positive number (got {config.StartPort}).");
        }

        // metricsMaxInMemory must be >= 0
        if (config.MetricsMaxInMemory.HasValue && config.MetricsMaxInMemory.Value < 0)
        {
            errors.Add($"metricsMaxInMemory cannot be negative (got {config.MetricsMaxInMemory}).");
        }

        // captureBuffer must be >= 0
        if (config.CaptureBuffer.HasValue && config.CaptureBuffer.Value < 0)
        {
            errors.Add($"captureBuffer cannot be negative (got {config.CaptureBuffer}).");
        }

        // Validate per-model settings
        if (config.Models != null)
        {
            foreach (var kvp in config.Models)
            {
                var modelId = kvp.Key;
                var model = kvp.Value;
                if (model == null) continue;

                // Validate checkEndpoint format
                if (!string.IsNullOrEmpty(model.CheckEndpoint))
                {
                    if (model.CheckEndpoint != "none" && !model.CheckEndpoint.StartsWith("/"))
                    {
                        errors.Add($"Model '{modelId}': checkEndpoint must start with '/' or be 'none' (got '{model.CheckEndpoint}').");
                    }
                }

                // Validate TTL per model
                if (model.Ttl.HasValue && model.Ttl.Value < 0)
                {
                    errors.Add($"Model '{modelId}': ttl cannot be negative (got {model.Ttl}).");
                }

                // Validate model timeouts
                if (model.Timeouts != null)
                {
                    if (model.Timeouts.Connect.HasValue && model.Timeouts.Connect.Value < 0)
                        errors.Add($"Model '{modelId}': timeouts.connect cannot be negative.");
                    if (model.Timeouts.Keepalive.HasValue && model.Timeouts.Keepalive.Value < 0)
                        errors.Add($"Model '{modelId}': timeouts.keepalive cannot be negative.");
                    if (model.Timeouts.IdleConn.HasValue && model.Timeouts.IdleConn.Value < 0)
                        errors.Add($"Model '{modelId}': timeouts.idleConn cannot be negative.");
                }

                // Validate aliases format
                if (model.Aliases != null)
                {
                    foreach (var alias in model.Aliases.Where(a => string.IsNullOrWhiteSpace(a)))
                    {
                        errors.Add($"Model '{modelId}': alias cannot be empty.");
                    }
                }

                // Validate concurrencyLimit
                if (model.ConcurrencyLimit.HasValue && model.ConcurrencyLimit.Value < 1)
                {
                    errors.Add($"Model '{modelId}': concurrencyLimit must be >= 1 (got {model.ConcurrencyLimit}).");
                }
            }
        }

        // Validate matrix config
        if (config.Matrix != null)
        {
            if (config.Matrix.Vars != null)
            {
                foreach (var kvp in config.Matrix.Vars)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
                    {
                        errors.Add($"Matrix vars: key and value cannot be empty (got key='{kvp.Key}', value='{kvp.Value}').");
                    }
                }
            }

            if (config.Matrix.EvictCosts != null)
            {
                foreach (var kvp in config.Matrix.EvictCosts)
                {
                    if (kvp.Value < 0)
                    {
                        errors.Add($"Matrix evict_costs: cost for '{kvp.Key}' cannot be negative (got {kvp.Value}).");
                    }
                }
            }

            if (config.Matrix.Sets != null)
            {
                foreach (var kvp in config.Matrix.Sets)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
                    {
                        errors.Add($"Matrix sets: key and value cannot be empty (got key='{kvp.Key}', value='{kvp.Value}').");
                    }
                }
            }
        }

        // Validate auto-update config
        if (config.AutoUpdate != null)
        {
            if (!string.IsNullOrEmpty(config.AutoUpdate.CheckInterval) &&
                !ValidCheckIntervals.Contains(config.AutoUpdate.CheckInterval))
            {
                errors.Add($"Invalid autoUpdate.checkInterval: '{config.AutoUpdate.CheckInterval}'. Valid values: daily, weekly, monthly, manual.");
            }
        }

        // Validate binaries config
        if (config.Binaries != null)
        {
            foreach (var kvp in config.Binaries)
            {
                var binaryName = kvp.Key;
                var binaryConfig = kvp.Value;

                if (!ValidBinaryNames.Contains(binaryName))
                {
                    errors.Add($"Unknown binary '{binaryName}' in binaries config. Valid binaries: {string.Join(", ", ValidBinaryNames)}");
                }

                if (binaryConfig != null && !string.IsNullOrEmpty(binaryConfig.Version))
                {
                    // Version must not be empty if set
                    if (binaryConfig.Version.Trim().Length == 0)
                    {
                        errors.Add($"Binary '{binaryName}': version cannot be whitespace.");
                    }
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Dry-run validation: serialize and re-parse to ensure round-trip integrity.
    /// Catches YAML serialization issues that structural/semantic can't detect.
    /// </summary>
    public static List<string> ValidateDryRun(LlamaSwapConfig config)
    {
        var errors = new List<string>();

        try
        {
            // Serialize to YAML
            var yaml = config.ToYaml();

            // Re-parse from YAML
            var reparsed = LlamaSwapConfig.FromYaml(yaml);

            // Verify models round-trip correctly
            if (config.Models != null && reparsed.Models != null)
            {
                if (config.Models.Count != reparsed.Models.Count)
                {
                    errors.Add($"Dry-run failed: model count mismatch ({config.Models.Count} -> {reparsed.Models.Count}).");
                }
                else
                {
                    foreach (var kvp in config.Models)
                    {
                        if (!reparsed.Models.TryGetValue(kvp.Key, out var reparsedModel))
                        {
                            errors.Add($"Dry-run failed: model '{kvp.Key}' not found after round-trip.");
                            continue;
                        }

                        // Compare key fields
                        if (kvp.Value?.Cmd != reparsedModel?.Cmd)
                        {
                            errors.Add($"Dry-run failed: model '{kvp.Key}' Cmd mismatch after round-trip.");
                        }

                        if (kvp.Value?.CheckEndpoint != reparsedModel?.CheckEndpoint)
                        {
                            errors.Add($"Dry-run failed: model '{kvp.Key}' CheckEndpoint mismatch after round-trip.");
                        }

                        if (kvp.Value?.Name != reparsedModel?.Name)
                        {
                            errors.Add($"Dry-run failed: model '{kvp.Key}' Name mismatch after round-trip.");
                        }

                        if (kvp.Value?.Ttl != reparsedModel?.Ttl)
                        {
                            errors.Add($"Dry-run failed: model '{kvp.Key}' Ttl mismatch after round-trip.");
                        }
                    }
                }
            }

            // Verify top-level fields
            if (config.StartPort != reparsed.StartPort)
                errors.Add("Dry-run failed: StartPort mismatch after round-trip.");

            if (config.HealthCheckTimeout != reparsed.HealthCheckTimeout)
                errors.Add("Dry-run failed: HealthCheckTimeout mismatch after round-trip.");

            if (config.LogLevel != reparsed.LogLevel)
                errors.Add("Dry-run failed: LogLevel mismatch after round-trip.");

            if (config.GlobalTTL != reparsed.GlobalTTL)
                errors.Add("Dry-run failed: GlobalTTL mismatch after round-trip.");

            if (config.SendLoadingState != reparsed.SendLoadingState)
                errors.Add("Dry-run failed: SendLoadingState mismatch after round-trip.");

            // Verify matrix round-trip
            if (config.Matrix != null)
            {
                if (reparsed.Matrix == null)
                {
                    errors.Add("Dry-run failed: Matrix config lost after round-trip.");
                }
                else
                {
                    if (config.Matrix.Vars != null && reparsed.Matrix.Vars != null)
                    {
                        if (config.Matrix.Vars.Count != reparsed.Matrix.Vars.Count)
                            errors.Add("Dry-run failed: Matrix vars count mismatch.");
                    }
                    if (config.Matrix.Sets != null && reparsed.Matrix.Sets != null)
                    {
                        if (config.Matrix.Sets.Count != reparsed.Matrix.Sets.Count)
                            errors.Add("Dry-run failed: Matrix sets count mismatch.");
                    }
                    if (config.Matrix.EvictCosts != null && reparsed.Matrix.EvictCosts != null)
                    {
                        if (config.Matrix.EvictCosts.Count != reparsed.Matrix.EvictCosts.Count)
                            errors.Add("Dry-run failed: Matrix evict_costs count mismatch.");
                    }
                }
            }

            // Verify auto-update round-trip
            if (config.AutoUpdate != null)
            {
                if (reparsed.AutoUpdate == null)
                {
                    errors.Add("Dry-run failed: AutoUpdate config lost after round-trip.");
                }
                else
                {
                    if (config.AutoUpdate.Enabled != reparsed.AutoUpdate.Enabled)
                        errors.Add("Dry-run failed: AutoUpdate.Enabled mismatch after round-trip.");
                    if (config.AutoUpdate.CheckOnStartup != reparsed.AutoUpdate.CheckOnStartup)
                        errors.Add("Dry-run failed: AutoUpdate.CheckOnStartup mismatch after round-trip.");
                    if (config.AutoUpdate.CheckInterval != reparsed.AutoUpdate.CheckInterval)
                        errors.Add("Dry-run failed: AutoUpdate.CheckInterval mismatch after round-trip.");
                    if (config.AutoUpdate.AutoDownload != reparsed.AutoUpdate.AutoDownload)
                        errors.Add("Dry-run failed: AutoUpdate.AutoDownload mismatch after round-trip.");
                }
            }

            // Verify binaries round-trip
            if (config.Binaries != null)
            {
                if (reparsed.Binaries == null)
                {
                    errors.Add("Dry-run failed: Binaries config lost after round-trip.");
                }
                else
                {
                    if (config.Binaries.Count != reparsed.Binaries.Count)
                        errors.Add("Dry-run failed: Binaries count mismatch.");
                    else
                    {
                        foreach (var kvp in config.Binaries)
                        {
                            if (!reparsed.Binaries.TryGetValue(kvp.Key, out var reparsedBinary))
                            {
                                errors.Add($"Dry-run failed: binary '{kvp.Key}' not found after round-trip.");
                                continue;
                            }
                            if (kvp.Value?.Enabled != reparsedBinary?.Enabled)
                                errors.Add($"Dry-run failed: binary '{kvp.Key}' Enabled mismatch after round-trip.");
                            if (kvp.Value?.Version != reparsedBinary?.Version)
                                errors.Add($"Dry-run failed: binary '{kvp.Key}' Version mismatch after round-trip.");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Dry-run failed: serialization/deserialization error: {ex.Message}");
        }

        return errors;
    }

    /// <summary>
    /// Quick validation for a single model's checkEndpoint.
    /// </summary>
    public static (bool IsValid, string? Error) ValidateCheckEndpoint(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint))
        {
            return (false, "checkEndpoint cannot be empty. Use '/' for a path or 'none' to disable.");
        }

        if (endpoint == "none")
        {
            return (true, null);
        }

        if (!endpoint.StartsWith("/"))
        {
            return (false, $"checkEndpoint must start with '/' or be 'none' (got '{endpoint}').");
        }

        return (true, null);
    }
}
