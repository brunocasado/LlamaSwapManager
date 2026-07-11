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
    // --- Commands ---
    private async Task ExecuteStartAsync()
    {
        await RunLifecycleCommandAsync(
            busyText: "Starting…",
            action: () => _processManager.StartAsync(),
            resultLabel: "start");
    }

    private async Task ExecuteStopAsync()
    {
        await RunLifecycleCommandAsync(
            busyText: "Stopping…",
            action: () => _processManager.StopAsync(),
            resultLabel: "stop");
    }

    private async Task ExecuteRestartAsync()
    {
        await RunLifecycleCommandAsync(
            busyText: "Restarting…",
            action: () => _processManager.RestartAsync(),
            resultLabel: "restart");
    }

    /// <summary>
    /// Shared Start/Stop/Restart plumbing: always leaves IsBusy=false and rebuilds button state,
    /// with a hard timeout so hung process I/O cannot freeze the UI forever.
    /// </summary>
    private async Task RunLifecycleCommandAsync(string busyText, Func<Task<bool>> action, string resultLabel)
    {
        IsBusy = true;
        StartButtonEnabled = false;
        StopButtonEnabled = false;
        RestartButtonEnabled = false;
        StatusText = busyText;
            ShowToast(busyText);

        try
        {
            // 20s ceiling covers unload + graceful + force kill; never block buttons forever.
            var ok = await Task.Run(async () =>
            {
                try
                {
                    return await action().WaitAsync(TimeSpan.FromSeconds(20));
                }
                catch (TimeoutException)
                {
                    OnLogMessage($"[ui] {resultLabel} timed out after 20s");
                    return false;
                }
            });
            OnLogMessage($"[ui] {resultLabel} result: {ok}");
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            ShowToast($"Error: {ex.Message}");
            OnLogMessage($"[ui] {resultLabel} error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            try
            {
                await RefreshRuntimeStateAsync();
                // Toast final runtime status (also mirrored in sidebar RUNTIME).
                ShowToast(StatusText);
            }
            catch (Exception ex)
            {
                OnLogMessage($"[ui] refresh after {resultLabel} failed: {ex.Message}");
                // Force terminal UI state even if detection hangs.
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsBusy = false;
                    Status = LlamaSwapStatus.Error;
                    StatusText = $"Status: error after {resultLabel}";
                    StartButtonEnabled = true;
                    StopButtonEnabled = false;
                    RestartButtonEnabled = true;
                    ShowToast(StatusText);
                });
            }
        }
    }

    private async Task RefreshRuntimeStateAsync()
    {
        try
        {
            await Task.Run(() => _processManager.RefreshPaths()).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch { /* path refresh is best-effort */ }

        // Bound detection so outer finally of lifecycle commands can always finish.
        try
        {
            var apiBaseUrl = await _processManager.DetectApiBaseUrlAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (apiBaseUrl != null)
                _processManager.ApiBaseUrl = apiBaseUrl;
            else
                _processManager.ApiBaseUrl = null;
        }
        catch
        {
            _processManager.ApiBaseUrl = null;
        }

        try
        {
            var llamaServerUrl = await _processManager.DetectLlamaServerBaseUrlAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (llamaServerUrl != null)
                _processManager.LlamaServerBaseUrl = llamaServerUrl;
            else
                _processManager.LlamaServerBaseUrl = null;
        }
        catch
        {
            _processManager.LlamaServerBaseUrl = null;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            AutoDetectPaths();
            UpdateUI();
        });
    }

    private void ExecuteRefresh()
    {
        StatusText = "Status: refreshing...";
        OnLogMessage("[ui] refresh requested");
        _ = RefreshRuntimeStateAsync();
    }

    public void ReportUiError(string message)
    {
        OnLogMessage($"[ui] {message}");
        ShowToast(message);
    }

    public void ReportUiInfo(string message)
    {
        OnLogMessage($"[ui] {message}");
        ShowToast(message);
    }

    /// <summary>Transient toast bubble (UI-only). Prefer this over StatusText for save/confirm feedback.</summary>
    public event Action<string>? ToastRequested;

    public void ShowToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        OnLogMessage($"[toast] {message}");
        ToastRequested?.Invoke(message);
    }


    private void CloseModelEditor()
    {
        if (SelectedModel?.IsNew == true)
        {
            ExecuteCancelModel();
            return;
        }
        foreach (var m in Models) m.IsSelected = false;
        SelectedModel = null;
        HasSelectedModel = false;
        IsNewModel = false;
        UpdateSelectedModelSourceLabel();
        (AddModelCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Reorder models in the UI collection; BuildConfigFromUI writes them in this order to config.yml.
    /// </summary>
    public void MoveModel(ModelEditItem? model, int delta)
    {
        if (model is null) return;
        var i = Models.IndexOf(model);
        if (i < 0) return;
        var j = i + delta;
        if (j < 0 || j >= Models.Count) return;
        Models.Move(i, j);
        PersistConfigToDisk(silent: true);
    }

    /// <summary>Move <paramref name="sourceId"/> to the index of <paramref name="targetId"/> (insert before target).</summary>
    public void ReorderModel(string? sourceId, string? targetId)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(targetId)) return;
        if (string.Equals(sourceId, targetId, StringComparison.Ordinal)) return;

        var from = -1;
        var to = -1;
        for (var i = 0; i < Models.Count; i++)
        {
            if (string.Equals(Models[i].ModelId, sourceId, StringComparison.Ordinal)) from = i;
            if (string.Equals(Models[i].ModelId, targetId, StringComparison.Ordinal)) to = i;
        }
        if (from < 0 || to < 0 || from == to) return;

        Models.Move(from, to);
        PersistConfigToDisk(silent: true);
    }

    public void ExecuteSelectModel(ModelEditItem model)
       {
           if (model == null) return;
         
           // If there's an unsaved new model, show warning and don't allow switching
           if (SelectedModel != null && SelectedModel.IsNew)
           {
               ShowToast("Save or cancel the current model before selecting another one.");
               return;
           }
         
           foreach (var m in Models) m.IsSelected = false;
           model.IsSelected = true;
           SelectedModel = model;
           HasSelectedModel = true;
           IsNewModel = false;
           ModelEditorSection = "essentials";
           UpdateSelectedModelSourceLabel();
       }

    private void ExecuteAddModel()
    {
        var newItem = new ModelEditItem
        {
            ModelId = $"model_{Models.Count + 1}",
            Name = "New Model",
            LlamaServerPath = LlamaServerPath,
            UseJinja = false,
            FitOn = false,
            NoMmap = false,
            // Fail-safe: new models force thinking off so it is not silently on by default.
            Reasoning = "off",
            IsNew = true
        };
        foreach (var m in Models) m.IsSelected = false;
        newItem.IsSelected = true;
        HookModelItem(newItem);
        Models.Add(newItem);
        SelectedModel = newItem;
        HasSelectedModel = true;
        IsNewModel = true;
        ModelEditorSection = "essentials";
        UpdateSelectedModelSourceLabel();
        EnsureMatrixModelCoverage();
        SyncEvictCostsWithCurrentVars(refreshPreview: false);
        UpdateConfigPreviewFromCurrentState();
        (AddModelCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }



    public void ExecuteCloneModel(ModelEditItem source)
    {
        if (source == null) return;

        if (SelectedModel?.IsNew == true)
        {
            ShowToast("Save or cancel the current new model before cloning another one.");
            return;
        }

        var baseId = string.IsNullOrWhiteSpace(source.ModelId) ? "model" : source.ModelId.Trim();
        var cloneId = GetUniqueModelId($"{baseId}_copy");
        var clone = source.CloneAs(cloneId);
        clone.IsNew = true;

        foreach (var m in Models) m.IsSelected = false;
        clone.IsSelected = true;
        HookModelItem(clone);
        Models.Add(clone);
        SelectedModel = clone;
        HasSelectedModel = true;
        IsNewModel = true;
        UpdateSelectedModelSourceLabel();
        EnsureMatrixModelCoverage();
        SyncEvictCostsWithCurrentVars(refreshPreview: false);
        UpdateConfigPreviewFromCurrentState();
        ShowToast($"Cloned {source.ModelId}. Adjust parameters, then click Save.");
        StatusColor = "#A6E3A1";
        (AddModelCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private string GetUniqueModelId(string preferred)
    {
        var candidate = preferred;
        var suffix = 2;
        while (Models.Any(m => string.Equals(m.ModelId, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{preferred}_{suffix}";
            suffix++;
        }
        return candidate;
    }

    private void HookModelItem(ModelEditItem model)
    {
        model.PropertyChanged -= OnModelItemPropertyChanged;
        model.PropertyChanged += OnModelItemPropertyChanged;
    }

    private void OnModelItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_matrixSyncDepth > 0) return;
        if (e.PropertyName is nameof(ModelEditItem.IsSelected) or nameof(ModelEditItem.IsNew)) return;

        _matrixSyncDepth++;
        try
        {
            if (e.PropertyName == nameof(ModelEditItem.ModelId))
            {
                EnsureMatrixModelCoverage();
                SyncEvictCostsWithCurrentVars(refreshPreview: false);
            }
            UpdateConfigPreviewFromCurrentState();
        }
        finally { _matrixSyncDepth--; }
    }

    private void ExecuteCancelModel()
    {
        if (SelectedModel == null) return;

        if (SelectedModel.IsNew && Models.Contains(SelectedModel))
        {
            Models.Remove(SelectedModel);
            SelectedModel.IsSelected = false;
            SelectedModel = null;
            HasSelectedModel = false;
            IsNewModel = false;
            ShowToast("New model cancelled.");
            StatusColor = "#FAB387";
            UpdateSelectedModelSourceLabel();
            EnsureMatrixModelCoverage();
            SyncEvictCostsWithCurrentVars(refreshPreview: false);
            UpdateConfigPreviewFromCurrentState();
            (AddModelCommand as RelayCommand)?.NotifyCanExecuteChanged();
            return;
        }

        ShowToast("Nothing to cancel: this model is already saved.");
        StatusColor = "#FAB387";
    }

    private void ExecuteDeleteModel()
    {
        if (SelectedModel == null || !Models.Contains(SelectedModel)) return;

        var deletedId = SelectedModel.ModelId;
        SelectedModel.IsSelected = false;
        Models.Remove(SelectedModel);
        SelectedModel = null;
        HasSelectedModel = false;
        IsNewModel = false;
        UpdateSelectedModelSourceLabel();

        // Remove deleted model from every existing combination, then rebuild YAML text.
        foreach (var combo in MatrixCombinations)
        {
            var stale = combo.Models
                .Where(m => string.Equals(m.ModelId, deletedId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var item in stale) combo.Models.Remove(item);
        }

        EnsureMatrixModelCoverage();
        SyncMatrixTextFromCombinations();
        SyncEvictCostsWithCurrentVars(refreshPreview: false);
        PersistConfigToDisk($"Model {deletedId} deleted from config.yml.");
        (AddModelCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private void ExecuteSaveModel()
    {
        if (SelectedModel == null) return;

        if (string.IsNullOrWhiteSpace(SelectedModel.ModelId))
        {
            ShowToast("Error: Model ID is required.");
            return;
        }

        var duplicate = Models.Any(m => !ReferenceEquals(m, SelectedModel) && string.Equals(m.ModelId, SelectedModel.ModelId, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            ShowToast($"Error: a model with ID {SelectedModel.ModelId} already exists.");
            return;
        }

        SelectedModel.IsNew = false;
        IsNewModel = false;
        EnsureMatrixModelCoverage();
        SyncEvictCostsWithCurrentVars(refreshPreview: false);
        PersistConfigToDisk("Model saved.");
        (AddModelCommand as RelayCommand)?.NotifyCanExecuteChanged();
        // Close the editor sheet after a successful save.
        CloseModelEditor();
    }

    private void UpdateSelectedModelSourceLabel()
    {
        if (SelectedModel == null)
        {
            SelectedModelSourceLabel = "No model selected";
            return;
        }

        if (!string.IsNullOrWhiteSpace(SelectedModel.ModelPath))
            SelectedModelSourceLabel = $"Local GGUF: {Path.GetFileName(SelectedModel.ModelPath)}";
        else if (!string.IsNullOrWhiteSpace(SelectedModel.HfModel))
        {
            var q = !string.IsNullOrWhiteSpace(SelectedModel.SelectedQuantization)
                ? $" [{SelectedModel.SelectedQuantization}]"
                : "";
            SelectedModelSourceLabel = $"Hugging Face GGUF: {SelectedModel.HfModel}{q}";
        }
        else
            SelectedModelSourceLabel = "Choose a local GGUF file or Hugging Face GGUF repo";
    }

    public void SetLocalModelPath(string path)
    {
        if (SelectedModel == null || string.IsNullOrWhiteSpace(path)) return;
        SelectedModel.ModelPath = path;

        SelectedModel.HfModel = "";
        IsModelPickerOpen = false;
        UpdateSelectedModelSourceLabel();
        ShowToast("Local model selected.");
    }

    private async Task ExecuteSearchHfModelsAsync()
    {
        var query = string.IsNullOrWhiteSpace(HfSearchQuery) ? SelectedModel?.HfModel : HfSearchQuery;
        if (string.IsNullOrWhiteSpace(query))
        {
            StatusText = "Type a query to search GGUF models on Hugging Face.";
            StatusColor = "#FAB387";
            return;
        }

        try
        {
            HfSearchResults.Clear();
            StatusText = "Searching GGUF models on Hugging Face...";
            StatusColor = "#89B4FA";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            var url = $"https://huggingface.co/api/models?search={Uri.EscapeDataString(query)}&filter=gguf&limit=12&sort=downloads&direction=-1";
            using var stream = await http.GetStreamAsync(url);
            var json = await JsonDocument.ParseAsync(stream);
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("modelId", out var id))
                {
                    var modelId = id.GetString();
                    if (!string.IsNullOrWhiteSpace(modelId)) HfSearchResults.Add(modelId);
                }
            }
            StatusText = HfSearchResults.Count == 0 ? "No GGUF models found." : $"{HfSearchResults.Count} GGUF model(s) found.";
            StatusColor = HfSearchResults.Count == 0 ? "#FAB387" : "#A6E3A1";
        }
        catch (Exception ex)
        {
            StatusText = $"HF search error: {ex.Message}";
            StatusColor = "#F38BA8";
        }
    }

    public void SetHfModel(string modelId)
    {
        if (SelectedModel == null || string.IsNullOrWhiteSpace(modelId)) return;
        SelectedModel.HfModel = modelId;
        SelectedModel.ModelPath = "";
        SelectedModel.SelectedQuantization = "";
        HfSearchQuery = modelId;
        IsModelPickerOpen = false;
        UpdateSelectedModelSourceLabel();
        ShowToast("Hugging Face model selected.");
    }

    /// <summary>
    /// Apply HF model selection. <paramref name="repoFileOrQuant"/> may be a quant tag
    /// (Q4_K_M), a bare filename, or a repo-relative path (subdir/file.gguf).
    /// Not every GGUF embeds a recognizable quant token — in that case we keep the file path.
    /// </summary>
    public void SetHfModelWithQuantization(string modelId, string repoFileOrQuant)
    {
        if (SelectedModel == null || string.IsNullOrWhiteSpace(modelId)) return;
        SelectedModel.HfModel = modelId;
        SelectedModel.ModelPath = "";

        var raw = (repoFileOrQuant ?? string.Empty).Trim().Replace('\\', '/');
        // Prefer known quant token from the leaf filename; otherwise keep relative path/filename for -hf repo:file
        var leaf = Path.GetFileName(raw);
        var quantFromName = ExtractQuantizationLabelForModel(leaf);
        if (!string.IsNullOrWhiteSpace(quantFromName))
            SelectedModel.SelectedQuantization = quantFromName;
        else if (raw.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            SelectedModel.SelectedQuantization = raw; // path or filename without a standard quant tag
        else if (!string.IsNullOrWhiteSpace(raw))
            SelectedModel.SelectedQuantization = Path.GetFileNameWithoutExtension(raw);
        else
            SelectedModel.SelectedQuantization = "";

        HfSearchQuery = modelId;
        IsModelPickerOpen = false;
        UpdateSelectedModelSourceLabel();
        ShowToast(string.IsNullOrWhiteSpace(SelectedModel.SelectedQuantization)
            ? "Hugging Face model selected."
            : $"Hugging Face model selected ({SelectedModel.SelectedQuantization}).");
    }

    /// <summary>Shared quant extraction used by VM (mirrors UI helper patterns).</summary>
    private static string? ExtractQuantizationLabelForModel(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var quantPatterns = new[]
        {
            "Q8_0","Q8_K","Q6_K","Q5_K_M","Q5_K_S","Q5_0","Q5_1",
            "Q4_K_M","Q4_K_S","Q4_0","Q4_1","Q3_K_M","Q3_K_S","Q3_K_L",
            "Q2_K","Q2_0","IQ4_XS","IQ4_NL","IQ3_XS","IQ3_S","IQ3_M",
            "IQ2_XS","IQ2_S","IQ2_M","IQ1_S","IQ1_M",
            "FP16","FP8_M","BF16","F32","F16","UD-Q4_K_XL","UD-Q5_K_XL","UD-Q6_K_XL","UD-Q8_K_XL"

        };
        foreach (var pattern in quantPatterns)
        {
            if (baseName.EndsWith(pattern, StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("-" + pattern, StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("_" + pattern, StringComparison.OrdinalIgnoreCase))
                return pattern;
        }
        var match = System.Text.RegularExpressions.Regex.Match(
            baseName,
            @"[-_]((UD-)?[QIq][A-Za-z]*\d[\w_]*|FP\d+[A-Z_]*|BF\d+|F\d+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private void ApplyHfModel(string? modelId)
    {
        SetHfModel(modelId ?? "");
    }

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

    private void OnMatrixTextChanged()
    {
        if (_isLoadingConfig || _matrixSyncDepth > 0) return;
        _matrixSyncDepth++;
        try
        {
            SyncMatrixCollectionsFromText();
            RebuildMatrixCombinationsFromSets();
            EnsureMatrixModelCoverage();
            SyncEvictCostsWithCurrentVars(refreshPreview: false);
            UpdateConfigPreviewFromCurrentState();
            AutoPersistMatrixToDisk();
        }
        finally { _matrixSyncDepth--; }
    }

    private void PersistConfigToDisk(string? successMessage = null, bool silent = false)
    {
        var configPath = !string.IsNullOrWhiteSpace(ConfigFilePath) ? ConfigFilePath : _processManager.ConfigPath;
        if (string.IsNullOrWhiteSpace(configPath))
        {
            if (!silent)
                ShowToast("Error: config.yml path is not set.");
            return;
        }

        var config = BuildConfigFromUI();
        _configService.SaveConfig(config, configPath);
        _rawConfig = config;
        ConfigPreview = config.ToYaml();
        // Never overwrite RUNTIME StatusText here — toast only (unless silent).
        if (!silent && !string.IsNullOrWhiteSpace(successMessage))
            ShowToast(successMessage);
    }

    private void SyncMatrixTextFromCollections()
    {
        MatrixVarsText = string.Join(Environment.NewLine, MatrixVars.Where(v => !string.IsNullOrWhiteSpace(v.Key) || !string.IsNullOrWhiteSpace(v.Value)).Select(v => $"{v.Key} = {v.Value}"));
        MatrixSetsText = string.Join(Environment.NewLine, MatrixSets.Where(v => !string.IsNullOrWhiteSpace(v.Key) || !string.IsNullOrWhiteSpace(v.Value)).Select(v => $"{v.Key} = {v.Value}"));
        EvictCostsText = string.Join(Environment.NewLine, EvictCosts
            .Where(v => !string.IsNullOrWhiteSpace(v.Key) && int.TryParse(v.Value, out var parsed) && parsed > 0)
            .Select(v => $"{v.Key} = {v.Value}"));
    }

    private void SyncMatrixCollectionsFromText()
    {
        MatrixVars.Clear();
        foreach (var item in ParseKeyValueLines(MatrixVarsText))
            MatrixVars.Add(CreateMatrixEntryItem(item.Key, item.Value, MatrixVars));

        MatrixSets.Clear();
        foreach (var item in ParseKeyValueLines(MatrixSetsText))
            MatrixSets.Add(CreateMatrixEntryItem(item.Key, item.Value, MatrixSets));

        EvictCosts.Clear();
        foreach (var item in ParseKeyValueLines(EvictCostsText))
        {
            var value = int.TryParse(item.Value, out var parsed) && parsed > 0 ? item.Value : string.Empty;
            EvictCosts.Add(CreateEvictCostItem(item.Key, "", value));
        }
    }

    private void OnEvictCostChanged()
    {
        if (_isLoadingConfig || _matrixSyncDepth > 0) return;
        _matrixSyncDepth++;
        try
        {
            // Do NOT call RefreshConfigPreview() here. That path rebuilds EvictCosts
            // from MatrixVarsText, which clears/recreates the ItemsControl and makes
            // the focused Cost TextBox lose focus on every typed character.
            EvictCostsText = string.Join(Environment.NewLine, EvictCosts
                .Where(v => !string.IsNullOrWhiteSpace(v.Key) && int.TryParse(v.Value, out var parsed) && parsed > 0)
                .Select(v => $"{v.Key} = {v.Value}"));
            UpdateConfigPreviewFromCurrentState();
            AutoPersistMatrixToDisk();
        }
        finally { _matrixSyncDepth--; }
    }

    private void SyncEvictCostsWithCurrentVars(bool refreshPreview = true)
    {
        var existingByAlias = EvictCosts
            .Where(e => !string.IsNullOrWhiteSpace(e.Key))
            .GroupBy(e => e.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var existingByModel = EvictCosts
            .Where(e => !string.IsNullOrWhiteSpace(e.ModelId))
            .GroupBy(e => e.ModelId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var vars = ParseKeyValueLines(MatrixVarsText).ToList();
        if (!vars.Any() && Models.Any(m => !string.IsNullOrWhiteSpace(m.ModelId)))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            vars = Models.Where(m => !string.IsNullOrWhiteSpace(m.ModelId))
                .Select(m =>
                {
                    var alias = MakeAlias(m.ModelId, used);
                    used.Add(alias);
                    return new KeyValuePair<string, string>(alias, m.ModelId);
                })
                .ToList();
        }

        EvictCosts.Clear();
        foreach (var kv in vars)
        {
            var alias = kv.Key.Trim();
            var modelId = kv.Value.Trim();
            existingByModel.TryGetValue(modelId, out var byModel);
            existingByAlias.TryGetValue(alias, out var byAlias);
            var previous = byModel ?? byAlias;
            var value = previous?.Value;
            if (!int.TryParse(value, out var parsed) || parsed <= 0) value = string.Empty;
            EvictCosts.Add(CreateEvictCostItem(alias, modelId, value));
        }
        EvictCostsText = string.Join(Environment.NewLine, EvictCosts
            .Where(v => !string.IsNullOrWhiteSpace(v.Key) && int.TryParse(v.Value, out var parsed) && parsed > 0)
            .Select(v => $"{v.Key} = {v.Value}"));
        if (refreshPreview) UpdateConfigPreviewFromCurrentState();
    }

    private static IEnumerable<KeyValuePair<string, string>> ParseKeyValueLines(string text)
    {
        foreach (var raw in (text ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
            var idx = line.IndexOf('=');
            if (idx < 0) continue;
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key))
                yield return new KeyValuePair<string, string>(key, value);
        }
    }

    private void ExecuteAddMatrixVar()
    {
        var item = CreateMatrixEntryItem("", "", MatrixVars);
        MatrixVars.Add(item);
        RefreshConfigPreview();
    }

    private void ExecuteAddMatrixSet()
    {
        var item = CreateMatrixEntryItem("", "", MatrixSets);
        MatrixSets.Add(item);
        RefreshConfigPreview();
    }

    private void ExecuteAddEvictCost()
    {
        var item = CreateEvictCostItem("", "", "");
        EvictCosts.Add(item);
        RefreshConfigPreview();
    }

    private void ExecuteCreateSetFromVars()
    {
        if (MatrixVars.Count == 0) return;
        
        var varKeys = MatrixVars.Select(v => v.Key).ToList();
        if (varKeys.Count == 0) return;
        
        var newItem = CreateMatrixEntryItem($"set_{MatrixSets.Count + 1}", string.Join(" & ", varKeys), MatrixSets);
        MatrixSets.Add(newItem);
        RebuildMatrixCombinationsFromSets();
        RefreshConfigPreview();
    }
    public void SyncMatrixFromVisualBuilder()
    {
        RefreshConfigPreview();
    }

    private MatrixEntryItem CreateMatrixEntryItem(string key, string value, ObservableCollection<MatrixEntryItem> parent)
    {
        var item = new MatrixEntryItem { Key = key, Value = value, ParentCollection = parent };
        item.Changed += OnMatrixEntryChanged;
        return item;
    }

    private EvictCostItem CreateEvictCostItem(string key, string modelId, string value)
    {
        var item = new EvictCostItem
        {
            Key = key,
            ModelId = modelId,
            Value = value,
            Priority = EvictCostItem.PriorityFromCost(value),
            ParentCollection = EvictCosts
        };
        item.Changed += OnEvictCostChanged;
        return item;
    }

    private MatrixCombinationItem CreateMatrixCombinationItem(string name)
    {
        var combo = new MatrixCombinationItem { Name = name, ParentCollection = MatrixCombinations };
        combo.Changed += OnMatrixCombinationChanged;
        return combo;
    }

    private MatrixModelSelectionItem CreateMatrixModelSelectionItem(string modelId, string alias, bool isSelected)
    {
        var item = new MatrixModelSelectionItem { ModelId = modelId, Alias = alias, IsSelected = isSelected };
        item.Changed += OnMatrixCombinationChanged;
        return item;
    }

    private void OnMatrixEntryChanged()
    {
        if (_matrixSyncDepth > 0) return;
        _matrixSyncDepth++;
        try
        {
            SyncMatrixTextFromCollections();
            RebuildMatrixCombinationsFromSets();
            SyncEvictCostsWithCurrentVars(refreshPreview: false);
            UpdateConfigPreviewFromCurrentState();
        }
        finally { _matrixSyncDepth--; }
    }

    private void OnMatrixCombinationChanged()
    {
        RefreshConfigPreview();
    }

    private List<(ModelEditItem model, string alias)> GetCurrentModelAliases()
    {
        var aliasesByModel = ParseKeyValueLines(MatrixVarsText)
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .GroupBy(kv => kv.Value.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Key.Trim(), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(aliasesByModel.Values, StringComparer.OrdinalIgnoreCase);
        var result = new List<(ModelEditItem model, string alias)>();
        foreach (var model in Models.Where(m => !string.IsNullOrWhiteSpace(m.ModelId)))
        {
            if (!aliasesByModel.TryGetValue(model.ModelId, out var alias) || string.IsNullOrWhiteSpace(alias))
            {
                alias = MakeAlias(model.ModelId, used);
                used.Add(alias);
            }
            result.Add((model, alias));
        }
        return result;
    }

    private void EnsureMatrixModelCoverage()
    {
        var modelAliases = GetCurrentModelAliases();
        MatrixVarsText = string.Join(Environment.NewLine, modelAliases.Select(x => $"{x.alias} = {x.model.ModelId}"));

        foreach (var combo in MatrixCombinations)
        {
            var selectedByModel = combo.Models
                .Where(m => m.IsSelected)
                .Select(m => m.ModelId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existingByModel = combo.Models
                .Where(m => !string.IsNullOrWhiteSpace(m.ModelId))
                .GroupBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            combo.Models.Clear();
            foreach (var (model, alias) in modelAliases)
            {
                var selected = selectedByModel.Contains(model.ModelId);
                combo.Models.Add(CreateMatrixModelSelectionItem(model.ModelId, alias, selected));
            }
        }
    }

    private void RebuildMatrixCombinationsFromSets()
    {
        MatrixCombinations.Clear();
        var aliases = ParseKeyValueLines(MatrixVarsText).ToDictionary(x => x.Key, x => x.Value);
        foreach (var set in ParseKeyValueLines(MatrixSetsText))
        {
            var selectedAliases = set.Value.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var combo = CreateMatrixCombinationItem(set.Key);
            foreach (var model in Models)
            {
                var alias = aliases.FirstOrDefault(x => x.Value == model.ModelId).Key;
                if (string.IsNullOrWhiteSpace(alias)) alias = model.ModelId;
                combo.Models.Add(CreateMatrixModelSelectionItem(model.ModelId, alias, selectedAliases.Contains(alias) || selectedAliases.Contains(model.ModelId)));
            }
            MatrixCombinations.Add(combo);
        }
    }

    private void SyncMatrixTextFromCombinations()
    {
        if (!MatrixCombinations.Any())
        {
            MatrixVarsText = BuildMatrixVarsTextFromModels();
            MatrixSetsText = string.Empty;
            return;
        }
        MatrixVarsText = BuildMatrixVarsTextFromModels();
        var aliasMap = ParseKeyValueLines(MatrixVarsText).ToDictionary(x => x.Value, x => x.Key);
        var setLines = new List<string>();
        foreach (var combo in MatrixCombinations.Where(c => !string.IsNullOrWhiteSpace(c.Name)))
        {
            var selected = combo.Models.Where(m => m.IsSelected).Select(m => aliasMap.TryGetValue(m.ModelId, out var a) ? a : m.ModelId).ToList();
            if (selected.Any()) setLines.Add($"{combo.Name.Trim()} = {string.Join(" & ", selected)}");
        }
        MatrixSetsText = string.Join(Environment.NewLine, setLines);
    }

    private string BuildMatrixVarsTextFromModels()
    {
        var aliases = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in Models.Where(m => !string.IsNullOrWhiteSpace(m.ModelId)))
        {
            var alias = MakeAlias(model.ModelId, used);
            used.Add(alias);
            aliases.Add($"{alias} = {model.ModelId}");
        }
        return string.Join(Environment.NewLine, aliases);
    }

    private static string MakeAlias(string modelId, HashSet<string> used)
    {
        var chars = new string(modelId.Where(char.IsLetterOrDigit).Take(1).ToArray()).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(chars)) chars = "m";
        var alias = chars;
        var i = 1;
        while (used.Contains(alias)) alias = $"m{i++}";
        return alias;
    }

    private void ExecuteAddMatrixCombination()
    {
        EnsureMatrixModelCoverage();
        var combo = CreateMatrixCombinationItem($"combo_{MatrixCombinations.Count + 1}");
        foreach (var (model, alias) in GetCurrentModelAliases())
        {
            combo.Models.Add(CreateMatrixModelSelectionItem(model.ModelId, alias, false));
        }
        MatrixCombinations.Add(combo);
        RefreshConfigPreview();
    }

    private void ExecuteGenerateMatrixVarsFromModels()
    {
        if (!Models.Any())
        {
            StatusText = "No models available to generate vars.";
            StatusColor = "#FAB387";
            return;
        }

        var lines = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 1;
        foreach (var model in Models.Where(m => !string.IsNullOrWhiteSpace(m.ModelId)))
        {
            var baseKey = new string(model.ModelId.Where(char.IsLetterOrDigit).Take(1).ToArray()).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(baseKey)) baseKey = $"m{index}";
            var key = baseKey;
            while (used.Contains(key)) key = $"m{index++}";
            used.Add(key);
            lines.Add($"{key} = {model.ModelId}");
        }
        MatrixVarsText = string.Join(Environment.NewLine, lines);
        SyncMatrixCollectionsFromText();
        EnsureMatrixModelCoverage();
        SyncEvictCostsWithCurrentVars(refreshPreview: false);
        UpdateConfigPreviewFromCurrentState();
        StatusText = "Vars generated from models.";
        StatusColor = "#A6E3A1";
    }

    private void ExecuteCreateAllModelsMatrixSet()
    {
        MatrixCombinations.Clear();
        var combo = CreateMatrixCombinationItem("all");
        foreach (var (model, alias) in GetCurrentModelAliases())
        {
            combo.Models.Add(CreateMatrixModelSelectionItem(model.ModelId, alias, true));
        }
        if (combo.Models.Any())
            MatrixCombinations.Add(combo);
        SyncMatrixTextFromCombinations();
        SyncEvictCostsWithCurrentVars(refreshPreview: false);
        UpdateConfigPreviewFromCurrentState();
        StatusText = combo.Models.Any() ? "Combination 'all' created." : "No models available for Matrix.";
        StatusColor = combo.Models.Any() ? "#A6E3A1" : "#FAB387";
    }

    /// <summary>
    /// Best-effort stop then forced application exit. Never hangs the Quit button:
    /// stop is budgeted, and Shutdown always runs.
    /// </summary>
    public async Task QuitApplicationAsync()
    {
        OnLogMessage("[ui] quit requested");
        try
        {
            // Always try stop when any llama process exists, not only when enum says Running.
            if (_processManager.IsRunning()
                || _processManager.Status is LlamaSwapStatus.Running or LlamaSwapStatus.Starting or LlamaSwapStatus.Stopping)
            {
                try
                {
                    await ExecuteStopAsync().WaitAsync(TimeSpan.FromSeconds(12));
                }
                catch (TimeoutException)
                {
                    OnLogMessage("[ui] quit: stop timed out — force killing processes");
                    try { await _processManager.ForceStopForQuitAsync(); } catch { }
                }
            }
            else
            {
                // Still clear orphans that may exist without matching Status enum.
                try { await _processManager.ForceStopForQuitAsync(); } catch { }
            }
        }
        catch (Exception ex)
        {
            OnLogMessage($"[ui] quit stop path error: {ex.Message}");
            try { await _processManager.ForceStopForQuitAsync(); } catch { }
        }
        finally
        {
            try
            {
                StopMetricsPolling();
                await StopLogStreamingAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch { }

            try
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                {
                    // Let window Closing complete (do not re-hide to tray).
                    try
                    {
                        var win = desktop.MainWindow;
                        win?.GetType().GetMethod("BeginExit")?.Invoke(win, null);
                    }
                    catch { }

                    desktop.Shutdown(0);
                }
            }
            catch (Exception ex)
            {
                OnLogMessage($"[ui] desktop.Shutdown failed: {ex.Message}");
            }

            // Hard guarantee: Avalonia/tray can leave the process alive after Shutdown.
            // Window X still only hides — this runs ONLY on explicit Quit.
            try { Environment.Exit(0); } catch { }
        }
    }


    private void RefreshLoadedModelsAsync()
    {
        var baseUrl = _processManager.ApiBaseUrl;
        if (string.IsNullOrEmpty(baseUrl)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var response = await http.GetAsync($"{baseUrl}/running");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var data = System.Text.Json.JsonSerializer.Deserialize<RunningResponse>(json);
                    if (data != null)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            LoadedModels.Clear();
                            foreach (var m in data.Running)
                                LoadedModels.Add(m);
                        });
                    }
                }
            }
            catch { /* ignore — models refresh silently */ }
        });
    }
}
