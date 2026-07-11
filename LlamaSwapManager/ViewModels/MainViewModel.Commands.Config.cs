using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaSwapManager.Models;
using LlamaSwapManager.Services;

namespace LlamaSwapManager.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private void ExecuteLoadConfig()
        {
            AutoDetectPaths();
            
            if (!string.IsNullOrEmpty(_processManager.ConfigPath))
            {
                LoadConfigFromPath(_processManager.ConfigPath);
            }
    
            _ = RefreshRuntimeStateAsync();
        }
    
        private Task ExecuteSaveConfigAsync()
        {
            var configPath = !string.IsNullOrEmpty(ConfigFilePath) ? ConfigFilePath : _processManager.ConfigPath;
            if (string.IsNullOrEmpty(configPath))
            {
                StatusText = "Error: No config path set";
                return Task.CompletedTask;
            }
    
            var config = BuildConfigFromUI();
            _configService.SaveConfig(config, configPath);
            _rawConfig = config;
            ConfigPreview = config.ToYaml();
            StatusText = "Config saved!";
            StatusColor = "#A6E3A1";
            return Task.CompletedTask;
        }
    
        private Task ExecuteSaveAndPreviewAsync()
        {
            var configPath = !string.IsNullOrEmpty(ConfigFilePath) ? ConfigFilePath : _processManager.ConfigPath;
            if (string.IsNullOrEmpty(configPath))
            {
                StatusText = "Error: No config path set";
                return Task.CompletedTask;
            }
    
            var config = BuildConfigFromUI();
            _configService.SaveConfig(config, configPath);
            _rawConfig = config;
            ConfigPreview = config.ToYaml();
            StatusText = "Config saved & preview updated!";
            StatusColor = "#A6E3A1";
            return Task.CompletedTask;
        }
    
        private LlamaSwapConfig BuildConfigFromUI()
        {
            // Start from the loaded config so fields not explicitly modeled by the UI
            // (cmdStop, proxy, checkEndpoint, env, unlisted, filters, timeouts, hooks,
            // peers, apiKeys, logToStdout, etc.) survive a visual edit/save cycle.
            var config = _rawConfig ?? new LlamaSwapConfig();
            var previousModels = _rawConfig?.Models;
    
            // Models
            config.Models = new Dictionary<string, ModelConfig>();
            foreach (var model in Models)
            {
                var modelConfig = previousModels != null && previousModels.TryGetValue(model.ModelId, out var existing)
                    ? existing
                    : new ModelConfig();
    
                modelConfig.Cmd = model.BuildCmd();
                modelConfig.Name = string.IsNullOrWhiteSpace(model.Name) ? null : model.Name;
                modelConfig.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description;
                modelConfig.Ttl = string.IsNullOrEmpty(model.Ttl) || model.Ttl == "0" ? null : int.Parse(model.Ttl);
                modelConfig.Aliases = string.IsNullOrEmpty(model.AliasesText)
                    ? null
                    : model.AliasesText.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(a => a.Trim()).Where(a => !string.IsNullOrEmpty(a)).ToList();
    
                config.Models[model.ModelId] = modelConfig;
            }
    
            // Matrix state is maintained live by the UI sync handlers.
            SyncMatrixTextFromCombinations();
            
            var vars = ParseKeyValueLines(MatrixVarsText).ToDictionary(v => v.Key.Trim(), v => v.Value.Trim());
            var sets = ParseKeyValueLines(MatrixSetsText).ToDictionary(s => s.Key.Trim(), s => s.Value.Trim());
            var costs = EvictCosts
                .Where(c => !string.IsNullOrWhiteSpace(c.Key) && int.TryParse(c.Value, out var parsed) && parsed > 0)
                .ToDictionary(c => c.Key.Trim(), c => int.Parse(c.Value.Trim()));
            if (sets.Any())
            {
                config.Matrix = new MatrixConfig
                {
                    Vars = vars.Any() ? vars : null,
                    Sets = sets,
                    EvictCosts = costs.Any() ? costs : null
                };
            }
            else
            {
                MatrixSetsText = string.Empty;
                config.Matrix = null;
            }
    
            // Settings
            config.StartPort = int.TryParse(StartPort, out var sp) ? sp : 5800;
            config.HealthCheckTimeout = int.TryParse(HealthCheckTimeout, out var hct) ? hct : 500;
            config.LogLevel = string.IsNullOrWhiteSpace(LogLevel) || LogLevel.StartsWith("Avalonia.") ? "debug" : LogLevel.Trim();
            config.GlobalTTL = string.IsNullOrEmpty(GlobalTtl) || GlobalTtl == "0" ? null : int.Parse(GlobalTtl);
            config.SendLoadingState = SendLoadingState;
            config.LogFile ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llama-swap", "upstream.log");
    
            // Auto-update settings — initialize if missing
            config.AutoUpdate ??= new AutoUpdateConfig();
            config.AutoUpdate.CudaVersion = string.IsNullOrWhiteSpace(CudaVersion) ? null : CudaVersion;
    
            return config;
        }
    
        /// <summary>
        /// Syncs all matrix/eviction state and updates the config preview in real-time.
        /// Call this from any property changed handler to keep the preview in sync.
        /// </summary>
        private void RefreshConfigPreview()
        {
            if (_matrixSyncDepth > 0) return;
            _matrixSyncDepth++;
            try
            {
                SyncMatrixTextFromCombinations();
                SyncEvictCostsWithCurrentVars(refreshPreview: false);
                UpdateConfigPreviewFromCurrentState();
                AutoPersistMatrixToDisk();
            }
            finally
            {
                _matrixSyncDepth--;
            }
        }
    
        private void UpdateConfigPreviewFromCurrentState()
        {
            ConfigPreview = BuildConfigFromUI().ToYaml();
        }
    
        private void AutoPersistMatrixToDisk()
        {
            if (_isLoadingConfig) return;
            var configPath = !string.IsNullOrWhiteSpace(ConfigFilePath) ? ConfigFilePath : _processManager.ConfigPath;
            if (string.IsNullOrWhiteSpace(configPath)) return;
    
            try
            {
                var config = BuildConfigFromUI();
                _configService.SaveConfig(config, configPath);
                _rawConfig = config;
                ConfigPreview = config.ToYaml();
                ShowToast("Matrix saved.");
            }
            catch (Exception ex)
            {
                ShowToast($"Matrix autosave failed: {ex.Message}");
            }
        }
    
        partial void OnMatrixVarsTextChanged(string value) => OnMatrixTextChanged();
        partial void OnMatrixSetsTextChanged(string value) => OnMatrixTextChanged();
        partial void OnEvictCostsTextChanged(string value) => OnMatrixTextChanged();
}
