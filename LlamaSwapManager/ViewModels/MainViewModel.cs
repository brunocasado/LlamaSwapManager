using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using LlamaSwapManager.Models;
using LlamaSwapManager.Services;

namespace LlamaSwapManager.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private readonly LlamaSwapProcessManager _processManager;
    private LlamaSwapConfig? _rawConfig;
    private int _matrixSyncDepth;
    private bool _isLoadingConfig;

    // Status
    [ObservableProperty] private string _statusText = "Status: checking...";
    [ObservableProperty] private string _statusColor = "#888888";
    [ObservableProperty] private LlamaSwapStatus _status;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _startButtonEnabled = true;
    [ObservableProperty] private bool _stopButtonEnabled = false;
    [ObservableProperty] private bool _restartButtonEnabled = true;

    // Paths — auto-detected
    [ObservableProperty] private string _llamaSwapExePath = "";
    [ObservableProperty] private string _configFilePath = "";
    [ObservableProperty] private string _llamaServerPath = "";
    [ObservableProperty] private bool _llamaSwapExeAutoDetected = false;
    [ObservableProperty] private bool _configFileAutoDetected = false;

    // Settings
    [ObservableProperty] private string _startPort = "5800";
    [ObservableProperty] private string _healthCheckTimeout = "500";
    [ObservableProperty] private string _logLevel = "info";
    [ObservableProperty] private string _globalTtl = "0";
    [ObservableProperty] private bool _sendLoadingState = true;

    // GPU backend selection
    [ObservableProperty] private string _selectedGpuBackend = "";
    [ObservableProperty] private string _gpuDetectionStatus = "";
    [ObservableProperty] private string _gpuDetectionStatusColor = "#888888";
    private readonly List<string> _gpuBackendOptions = new();
    public IReadOnlyList<string> GpuBackendOptions => _gpuBackendOptions;

    // CUDA version selection (shown when CUDA backend is selected)
    [ObservableProperty] private string _cudaVersion = "12.4";
    [ObservableProperty] private string _cudaVersionStatus = "";
    [ObservableProperty] private string _cudaVersionStatusColor = "#888888";
    [ObservableProperty] private bool _isCudaVersionVisible;
    private readonly List<string> _cudaVersionOptions = new() { "12.4", "13.3" };
    public IReadOnlyList<string> CudaVersionOptions => _cudaVersionOptions;

    // Logs
    [ObservableProperty] private ObservableCollection<string> _logMessages = new();
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _upstreamLogText = "";
    private readonly Queue<string> _globalLogLines = new();
    private readonly Queue<string> _upstreamLogLines = new();
    private const int MaxLogLines = 5000;
    /// <summary>
    /// Maximum lines displayed in each log panel. Keeps UI string allocation bounded.
    /// </summary>
    private const int MaxDisplayLines = 500;
    // Cached compiled regex for filters
    private System.Text.RegularExpressions.Regex? _cachedUpstreamRegex;
    private string? _cachedUpstreamRegexText;

    // Models
    [ObservableProperty] private ObservableCollection<ModelEditItem> _models = new();
    [ObservableProperty] private ModelEditItem? _selectedModel;
    [ObservableProperty] private bool _hasSelectedModel;
    [ObservableProperty] private bool _isNewModel = false;
    [ObservableProperty] private bool _isSaving = false;
    [ObservableProperty] private string _hfSearchQuery = "";
    [ObservableProperty] private ObservableCollection<string> _hfSearchResults = new();
    [ObservableProperty] private bool _isModelPickerOpen;
    [ObservableProperty] private string _selectedModelSourceLabel = "No model selected";

    // Matrix
    [ObservableProperty] private ObservableCollection<MatrixEntryItem> _matrixVars = new();
    [ObservableProperty] private ObservableCollection<MatrixEntryItem> _matrixSets = new();
    [ObservableProperty] private ObservableCollection<EvictCostItem> _evictCosts = new();
    [ObservableProperty] private string _matrixVarsText = "";
    [ObservableProperty] private string _matrixSetsText = "";
    [ObservableProperty] private string _evictCostsText = "";
    [ObservableProperty] private ObservableCollection<MatrixCombinationItem> _matrixCombinations = new();

    // Config preview
    [ObservableProperty] private string _configPreview = "";

    // Metrics navigation
    [ObservableProperty] private string _currentView = "models";
    [ObservableProperty] private string _modelEditorSection = "essentials";
    [ObservableProperty] private long _prefillTokens;
    [ObservableProperty] private long _decodeTokens;
    [ObservableProperty] private double _tokensPerSecond;
    [ObservableProperty] private int _activeSlots;

    // Log filtering (per-window regex)
    [ObservableProperty] private string _upstreamLogFilterText = "";

    private MetricsService? _metricsService;
    private CancellationTokenSource? _metricsCts;
    private LogStreamService? _logStreamService;
    private CancellationTokenSource? _logStreamCts;

    // Real-time TPS extracted from upstream logs
    private static readonly System.Text.RegularExpressions.Regex s_tpsRegex =
        new(@"n_decoded\s*=\s*\d+\s*,\s*tg\s*=\s*([\d.]+)\s*t/s",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Loaded models from llama-swap /running
    [ObservableProperty]
    private ObservableCollection<LoadedModelInfo> _loadedModels = new();

    // Update subsystem
    public UpdateViewModel UpdateViewModel { get; }

    public enum AppView { Models, Matrix, Logs, Metrics }


    public IReadOnlyList<string> AutoOnOffOptions { get; } = new[] { "", "auto", "on", "off" };
    public IReadOnlyList<string> SplitModeOptions { get; } = new[] { "", "none", "layer", "row", "tensor" };
    public IReadOnlyList<string> CacheTypeOptions { get; } = new[] { "", "f16", "q8_0", "q4_0", "q4_1", "q5_0", "q5_1", "iq4_nl", "bf16", "f32" };
    // Empty/(default) omits --reasoning (server/model default). Explicit off/on/auto always emit.
    public IReadOnlyList<string> ReasoningOptions { get; } = new[] { "", "auto", "on", "off" };
    public IReadOnlyList<string> ReasoningFormatOptions { get; } = new[] { "", "none", "deepseek", "qwen3", "auto" };
    // Editable combo: free-type numeric N also accepted via IsEditable combobox.
    public IReadOnlyList<string> GpuLayersOptions { get; } = new[] { "", "auto", "all", "0", "20", "30", "40", "50", "60", "80", "99" };
    public IReadOnlyList<string> ChatTemplatePresets { get; } = new[]
    {
        "",
        "chatml",
        "llama2",
        "llama3",
        "mistral",
        "gemma",
        "command-r",
        "deepseek",
        "vicuna",
    };
    public IReadOnlyList<string> CommonSamplersOptions { get; } = new[] { "", "penalties;dry;top_n_sigma;top_k;typ_p;top_p;min_p;xtc;temperature", "penalties;top_k;top_p;min_p;temperature", "top_k;top_p;temperature" };

    // Commands
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand AddModelCommand { get; }
    public ICommand SelectModelCommand { get; }
    public ICommand DeleteModelCommand { get; }
    public ICommand CancelModelCommand { get; }
    public ICommand SaveModelCommand { get; }
    public ICommand SearchHfModelsCommand { get; }
    public ICommand ApplyHfModelCommand { get; }
    public ICommand OpenModelPickerCommand { get; }
    public ICommand CloseModelPickerCommand { get; }
    public ICommand LoadConfigCommand { get; }
    public ICommand SaveConfigCommand { get; }
    public ICommand SaveAndPreviewCommand { get; }
    public ICommand AddMatrixVarCommand { get; }
    public ICommand AddMatrixSetCommand { get; }
    public ICommand AddEvictCostCommand { get; }
    public ICommand CreateSetFromVarsCommand { get; }
    public ICommand GenerateMatrixVarsFromModelsCommand { get; }
    public ICommand CreateAllModelsMatrixSetCommand { get; }
    public ICommand AddMatrixCombinationCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand NavigateToMetricsCommand { get; }
    public ICommand NavigateToModelsCommand { get; }
    public ICommand NavigateToMatrixCommand { get; }
    public ICommand NavigateToLogsCommand { get; }
    public ICommand NavigateToSettingsCommand { get; }
    public ICommand NavigateToUpdatesCommand { get; }
    public ICommand NavigateToConfigPreviewCommand { get; }
    public ICommand CloseModelEditorCommand { get; }
    public ICommand MoveModelUpCommand { get; }
    public ICommand MoveModelDownCommand { get; }
    public ICommand SetModelEditorSectionCommand { get; }

    // CanExecute
    public bool CanStart => StartButtonEnabled;
    public bool CanStop => StopButtonEnabled;
    public bool CanRestart => RestartButtonEnabled;
    public bool CanAddModel => SelectedModel?.IsNew != true;

    public MainViewModel()
    {
        _configService = new ConfigService();
        _processManager = new LlamaSwapProcessManager();
        _configService.ConfigLoaded += OnConfigLoaded;
        _processManager.StatusChanged += OnProcessStatusChanged;
        _processManager.LogMessage += OnLogMessage;

        StartCommand = new AsyncRelayCommand(ExecuteStartAsync);
        StopCommand = new AsyncRelayCommand(ExecuteStopAsync);
        RestartCommand = new AsyncRelayCommand(ExecuteRestartAsync);
        RefreshCommand = new RelayCommand(ExecuteRefresh);
        AddModelCommand = new RelayCommand(ExecuteAddModel, () => CanAddModel);
        SelectModelCommand = new RelayCommand<ModelEditItem?>(model => { if (model != null) ExecuteSelectModel(model); });
        DeleteModelCommand = new RelayCommand(ExecuteDeleteModel);
        CancelModelCommand = new RelayCommand(ExecuteCancelModel);
        SaveModelCommand = new RelayCommand(ExecuteSaveModel);
        SearchHfModelsCommand = new AsyncRelayCommand(ExecuteSearchHfModelsAsync);
        ApplyHfModelCommand = new RelayCommand<string>(ApplyHfModel);
        OpenModelPickerCommand = new RelayCommand(() => IsModelPickerOpen = true);
        CloseModelPickerCommand = new RelayCommand(() => IsModelPickerOpen = false);
        LoadConfigCommand = new RelayCommand(ExecuteLoadConfig);
        SaveConfigCommand = new AsyncRelayCommand(ExecuteSaveConfigAsync);
        SaveAndPreviewCommand = new AsyncRelayCommand(ExecuteSaveAndPreviewAsync);
        AddMatrixVarCommand = new RelayCommand(ExecuteAddMatrixVar);
        AddMatrixSetCommand = new RelayCommand(ExecuteAddMatrixSet);
        AddEvictCostCommand = new RelayCommand(ExecuteAddEvictCost);
        CreateSetFromVarsCommand = new RelayCommand(ExecuteCreateSetFromVars);
        GenerateMatrixVarsFromModelsCommand = new RelayCommand(ExecuteGenerateMatrixVarsFromModels);
        CreateAllModelsMatrixSetCommand = new RelayCommand(ExecuteCreateAllModelsMatrixSet);
        AddMatrixCombinationCommand = new RelayCommand(ExecuteAddMatrixCombination);
        ClearLogCommand = new RelayCommand(() =>
                 {
                     LogMessages.Clear();
                      LogText = "";
                      UpstreamLogText = "";
                      _globalLogLines.Clear();
                      _upstreamLogLines.Clear();
                      _logStreamService?.ClearLogs();
                      UpstreamLogFilterText = "";
                     });
        NavigateToMetricsCommand = new RelayCommand(() => CurrentView = "metrics");
        NavigateToModelsCommand = new RelayCommand(() => CurrentView = "models");
        NavigateToMatrixCommand = new RelayCommand(() => CurrentView = "matrix");
        NavigateToLogsCommand = new RelayCommand(() => CurrentView = "metrics"); // metrics hosts live logs
        NavigateToSettingsCommand = new RelayCommand(() => CurrentView = "settings");
        NavigateToUpdatesCommand = new RelayCommand(() => CurrentView = "updates");
        NavigateToConfigPreviewCommand = new RelayCommand(() => CurrentView = "preview");
        CloseModelEditorCommand = new RelayCommand(CloseModelEditor);
        MoveModelUpCommand = new RelayCommand<ModelEditItem?>(m => MoveModel(m, -1));
        MoveModelDownCommand = new RelayCommand<ModelEditItem?>(m => MoveModel(m, +1));
        SetModelEditorSectionCommand = new RelayCommand<string?>(section =>
        {
            if (!string.IsNullOrWhiteSpace(section))
                ModelEditorSection = section.Trim();
        });

        // Auto-detect paths first
        AutoDetectPaths();

        // Set default llama-server path
        var defaultServerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".llama", "llama-server");
        if (File.Exists(defaultServerPath))
            LlamaServerPath = defaultServerPath;

         // Initialize update subsystem after paths are resolved
        // ExecutablePath is the full binary path — extract directory for UpdateViewModel
        var updateDir = _processManager.ExecutablePath != null
            ? Path.GetDirectoryName(_processManager.ExecutablePath)!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llama-swap");
        var llamaCppDir = _processManager.LlamaCppDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llama");
        UpdateViewModel = new UpdateViewModel(updateDir, llamaCppDir, message => OnLogMessage(message), CudaVersion);

        // Detect GPU backends
        DetectGpuBackends();

        // Detect CUDA version
        DetectCudaVersion();

        Status = LlamaSwapStatus.Stopped;
        UpdateUI();

        // Auto-load config if path is known
        if (!string.IsNullOrEmpty(_processManager.ConfigPath))
        {
            LoadConfigFromPath(_processManager.ConfigPath);
        }

        _ = RefreshRuntimeStateAsync();
    }

    private void AutoDetectPaths()
    {
        _processManager.ResolvePaths();
        
        if (!string.IsNullOrEmpty(_processManager.LlamaSwapExePath))
        {
            LlamaSwapExePath = _processManager.LlamaSwapExePath;
            LlamaSwapExeAutoDetected = true;
        }
        
        if (!string.IsNullOrEmpty(_processManager.ConfigPath))
        {
            ConfigFilePath = _processManager.ConfigPath;
            ConfigFileAutoDetected = true;
        }
    }

    private void DetectGpuBackends()
    {
        try
        {
            var backends = GpuDetectionSettings.GetAvailableBackends();
            _gpuBackendOptions.Clear();

            foreach (var backend in backends)
            {
                var displayName = $"{backend.Name} ({backend.Detail})";
                _gpuBackendOptions.Add(displayName);
            }

            // Set default to first available (highest priority)
            if (_gpuBackendOptions.Count > 0)
            {
                SelectedGpuBackend = _gpuBackendOptions[0];
                GpuDetectionStatus = $"Auto-detected: {backends[0].Name}";
                GpuDetectionStatusColor = "#A6E3A1";
            }
            else
            {
                _gpuBackendOptions.Add("CPU Only (no GPU detected)");
                SelectedGpuBackend = _gpuBackendOptions[0];
                GpuDetectionStatus = "No GPU detected — using CPU";
                GpuDetectionStatusColor = "#F9E2AF";
            }
        }
        catch (Exception ex)
        {
            GpuDetectionStatus = $"Error detecting GPU: {ex.Message}";
            GpuDetectionStatusColor = "#F38BA8";
            _gpuBackendOptions.Clear();
            _gpuBackendOptions.Add("CPU Only (detection failed)");
            SelectedGpuBackend = _gpuBackendOptions[0];
        }
    }

    private void DetectCudaVersion()
    {
        // Fixed options — user picks the CUDA version for llama.cpp builds
        // llama.cpp bundles its own CUDA DLLs, so system detection is irrelevant
        CudaVersion = "12.4";
        CudaVersionStatus = "llama.cpp bundles its own CUDA runtime DLLs";
        CudaVersionStatusColor = "#A6ADC8";
        UpdateCudaVersionVisibility();
    }

    private void UpdateCudaVersionVisibility()
    {
        IsCudaVersionVisible = SelectedGpuBackend != null && SelectedGpuBackend.Contains("Cuda", StringComparison.OrdinalIgnoreCase);
        if (!IsCudaVersionVisible)
        {
            CudaVersion = "12.4";
            CudaVersionStatus = "";
        }
        else
        {
            CudaVersionStatus = "llama.cpp bundles its own CUDA runtime DLLs";
            CudaVersionStatusColor = "#A6ADC8";
        }
    }

    private void LoadConfigFromPath(string configPath)
    {
        var config = ConfigService.LoadConfig(configPath);
        if (config != null)
        {
            _rawConfig = config;
            ParseConfigToUI(config);
        }
    }

    private void OnConfigLoaded(LlamaSwapConfig config)
    {
        _rawConfig = config;
        ParseConfigToUI(config);
    }

    private void ParseConfigToUI(LlamaSwapConfig config)
    {
        _isLoadingConfig = true;
        try
        {
        // Parse models
        Models.Clear();
        if (config.Models != null)
        {
            foreach (var kvp in config.Models)
            {
                var model = ModelEditItem.Parse(kvp.Key, kvp.Value);
                HookModelItem(model);
                Models.Add(model);
            }
        }

        // Parse matrix
        MatrixVars.Clear();
        MatrixSets.Clear();
        EvictCosts.Clear();

        if (config.Matrix != null)
        {
            if (config.Matrix.Vars != null)
                foreach (var kvp in config.Matrix.Vars)
                    MatrixVars.Add(CreateMatrixEntryItem(kvp.Key, kvp.Value, MatrixVars));

            if (config.Matrix.Sets != null)
                foreach (var kvp in config.Matrix.Sets)
                    MatrixSets.Add(CreateMatrixEntryItem(kvp.Key, kvp.Value, MatrixSets));

            if (config.Matrix.EvictCosts != null)
                 foreach (var kvp in config.Matrix.EvictCosts)
                 {
                     EvictCosts.Add(CreateEvictCostItem(kvp.Key, "", kvp.Value.ToString() ?? "0"));
                 }
        }

        // Parse settings
        if (config.StartPort.HasValue) StartPort = config.StartPort.Value.ToString();
        if (config.HealthCheckTimeout.HasValue) HealthCheckTimeout = config.HealthCheckTimeout.Value.ToString();
        if (!string.IsNullOrEmpty(config.LogLevel)) LogLevel = config.LogLevel;
        if (config.GlobalTTL.HasValue) GlobalTtl = config.GlobalTTL.Value.ToString();
        if (config.SendLoadingState.HasValue) SendLoadingState = config.SendLoadingState.Value;

        // Parse auto-update CUDA version preference
        if (config.AutoUpdate != null && !string.IsNullOrEmpty(config.AutoUpdate.CudaVersion))
        {
            var savedVersion = config.AutoUpdate.CudaVersion;
            if (_cudaVersionOptions.Contains(savedVersion))
            {
                CudaVersion = savedVersion;
            }
        }

        SyncMatrixTextFromCollections();
        RebuildMatrixCombinationsFromSets();
        SyncEvictCostsWithCurrentVars(refreshPreview: false);

        // Generate config preview from the same UI state that Save uses.
        UpdateConfigPreviewFromCurrentState();

        UpdateUI();
        }
        finally
        {
            _isLoadingConfig = false;
        }
    }

    private void OnProcessStatusChanged(LlamaSwapStatus newStatus)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnProcessStatusChanged(newStatus));
            return;
        }

        Status = newStatus;
        UpdateUI();
    }

    private void OnLogMessage(string message)
    {
        // Write to file logger (always, even before UI is ready)
        FileLogger.Instance.Log(message);

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnLogMessage(message));
            return;
        }

        _globalLogLines.Enqueue(message);
        while (_globalLogLines.Count > MaxLogLines) _globalLogLines.Dequeue();
        LogMessages.Add(message);

        // Throttle LogText updates to every 10 messages to reduce UI churn
        if (LogMessages.Count % 10 == 0)
            LogText = string.Join("\n", _globalLogLines.ToArray().Skip(Math.Max(0, _globalLogLines.Count - 500)));

        // Split messages by source:
        // [out] = stdout from llama-swap process (upstream llama-server logs proxied through)
        // [err] = stderr from llama-swap process (llama-swap proxy logs)
        // [manager]/[ui] = Manager-internal messages (not process output)
        // [out] = proxy stdout, goes to global log only, NOT upstream panel
        // (upstream panel is exclusively fed by LogStreamService SSE)
        if (message.StartsWith("[out] "))
        {
            // [out] = proxy stdout, goes to global log only, NOT upstream panel
        }
        // [manager] and [ui] messages go to global log only, not to specific panels
    }

    partial void OnCurrentViewChanged(string value)
    {
        // No-op hook — bindings re-evaluate via generated PropertyChanged on CurrentView.
        OnLogMessage($"[ui] navigate → {value}");
    }

    private void UpdateUI()
    {
        // Prefer live process/API truth, but never collapse in-flight Starting/Stopping mid-command.
        var processRunning = _processManager.IsLlamaSwapProcessRunning();
        var apiUp = _processManager.DetectedApiBaseUrl is not null;

        if (Status is not (LlamaSwapStatus.Starting or LlamaSwapStatus.Stopping))
        {
            if (apiUp || processRunning)
                Status = LlamaSwapStatus.Running;
            else if (Status is LlamaSwapStatus.Running or LlamaSwapStatus.Error)
                Status = LlamaSwapStatus.Stopped;
        }
        else
        {
            // While busy, if the actual desired end-state is already observable, promote it.
            if (Status == LlamaSwapStatus.Starting && (apiUp || processRunning))
                Status = LlamaSwapStatus.Running;
            else if (Status == LlamaSwapStatus.Stopping && !apiUp && !processRunning && !_processManager.IsRunning())
                Status = LlamaSwapStatus.Stopped;
        }

        StatusText = _processManager.DetectedApiBaseUrl is not null
            ? $"Status: running ({_processManager.DetectedApiBaseUrl})"
            : $"Status: {Status.ToString().ToLower()}";

        // Buttons follow terminal process state; Starting/Stopping still show busy for feedback.
        IsBusy = Status is LlamaSwapStatus.Starting or LlamaSwapStatus.Stopping;
        StartButtonEnabled = Status != LlamaSwapStatus.Running && !IsBusy;
        StopButtonEnabled = (Status == LlamaSwapStatus.Running || processRunning || apiUp) && !IsBusy;
        RestartButtonEnabled = !IsBusy;

        switch (Status)
        {
            case LlamaSwapStatus.Running:
                StatusColor = "#A6E3A1";
                StartMetricsPolling();
                _ = StartLogStreamingAsync(); // StartLogStreamingAsync stops old stream internally
                break;
            case LlamaSwapStatus.Error:
                StatusColor = "#F38BA8";
                StopMetricsPolling();
                _ = StopLogStreamingAsync();
                break;
            default:
                StatusColor = "#888888";
                StopMetricsPolling();
                _ = StopLogStreamingAsync();
                break;
        }

        (StartCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (StopCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (RestartCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (AddModelCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

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
        PersistConfigToDisk("Model order updated.");
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
        PersistConfigToDisk("Model order updated.");
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

    private void PersistConfigToDisk(string successMessage)
    {
        var configPath = !string.IsNullOrWhiteSpace(ConfigFilePath) ? ConfigFilePath : _processManager.ConfigPath;
        if (string.IsNullOrWhiteSpace(configPath))
        {
            ShowToast("Error: config.yml path is not set.");
            return;
        }

        var config = BuildConfigFromUI();
        _configService.SaveConfig(config, configPath);
        _rawConfig = config;
        ConfigPreview = config.ToYaml();
        // Toast for user feedback — do not hijack the top/runtime status chip.
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

    // --- Metrics polling ---
    private void StartMetricsPolling()
    {
        StopMetricsPolling();

        // Use llama-server URL for metrics (not llama-swap which has different metrics)
        var baseUrl = _processManager.LlamaServerBaseUrl ?? _processManager.DetectedApiBaseUrl;
        if (string.IsNullOrEmpty(baseUrl)) return;

        _metricsService = new MetricsService(new HttpClient { Timeout = TimeSpan.FromSeconds(2) });
        _metricsService.SetApiBaseUrl(baseUrl);
        _metricsCts = new CancellationTokenSource();
        _ = PollMetricsAsync(_metricsCts.Token);
    }

    private void StopMetricsPolling()
    {
        _metricsCts?.Cancel();
        _metricsCts?.Dispose();
        _metricsCts = null;
        _metricsService = null;
    }

    // --- Log streaming (llama-swap /logs/stream/upstream) ---
    // The SSE endpoint is generic (not model-specific). We keep a single persistent
    // connection alive and only reconnect if it actually dies.
    private async Task StartLogStreamingAsync()
    {
        var baseUrl = _processManager.DetectedApiBaseUrl;
        if (string.IsNullOrEmpty(baseUrl))
            return;

        // If the stream is already alive on the same URL, do nothing.
        if (_logStreamService?.IsRunning == true)
            return;

        // Stream is dead or first start — create a new connection.
        if (_logStreamService != null)
        {
            _logStreamService.LogBatchReceived -= OnUpstreamLogBatchReceived;
            _logStreamService.Dispose();
            _logStreamService = null;
        }
        _logStreamCts?.Cancel();
        _logStreamCts?.Dispose();
        _logStreamCts = null;

        _logStreamService = new LogStreamService(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            baseUrl);
        _logStreamService.LogBatchReceived += OnUpstreamLogBatchReceived;
        _logStreamCts = new CancellationTokenSource();

        try
        {
            await _logStreamService.StartAsync("upstream", _logStreamCts.Token);
        }
        catch { /* stream may fail if API is temporarily unavailable */ }
    }

    private async Task StopLogStreamingAsync()
    {
        if (_logStreamService != null)
        {
            _logStreamService.LogBatchReceived -= OnUpstreamLogBatchReceived;
            await _logStreamService.StopAsync();
            _logStreamService.Dispose();
            _logStreamService = null;
        }
        _logStreamCts?.Cancel();
        _logStreamCts?.Dispose();
        _logStreamCts = null;
    }

    /// <summary>
    /// Receives a batch of upstream log lines from the background stream task.
    /// Posts a single UI update for the entire batch — not one per line.
    /// </summary>
    private void OnUpstreamLogBatchReceived(IReadOnlyList<string> batch)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnUpstreamLogBatchReceived(batch));
            return;
        }

        // Extract real-time tokens/sec from upstream log lines and push to UI immediately
        foreach (var line in batch)
        {
            var match = s_tpsRegex.Match(line);
            if (match.Success && double.TryParse(match.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var tps))
            {
                TokensPerSecond = tps;
            }
        }

        // Enqueue all lines
        foreach (var line in batch)
        {
            _upstreamLogLines.Enqueue(line);
        }
        while (_upstreamLogLines.Count > MaxLogLines) _upstreamLogLines.Dequeue();

        UpdateFilteredLogTexts(updateUpstream: true);
    }

    private void UpdateFilteredLogTexts(bool updateUpstream = true)
    {
        if (updateUpstream)
        {
            // Take only the last MaxDisplayLines to keep UI string allocation bounded
            var displayLines = _upstreamLogLines.Count > MaxDisplayLines
                ? _upstreamLogLines.Skip(_upstreamLogLines.Count - MaxDisplayLines)
                : _upstreamLogLines;

            if (string.IsNullOrWhiteSpace(UpstreamLogFilterText))
            {
                UpstreamLogText = string.Join("\n", displayLines);
            }
            else
            {
                try
                {
                    var regex = GetOrBuildRegex(ref _cachedUpstreamRegex, ref _cachedUpstreamRegexText, UpstreamLogFilterText);
                    var filtered = new List<string>();
                    foreach (var line in displayLines)
                    {
                        if (regex.IsMatch(line))
                            filtered.Add(line);
                    }
                    UpstreamLogText = string.Join("\n", filtered);
                }
                catch
                {
                    UpstreamLogText = string.Join("\n", displayLines);
                }
            }
        }
    }

    private static Regex GetOrBuildRegex(ref Regex? cached, ref string? cachedText, string pattern)
    {
        if (cached is not null && cachedText == pattern)
            return cached;

        cachedText = pattern;
        cached = new Regex(pattern, RegexOptions.Compiled | RegexOptions.Multiline);
        return cached;
    }

    partial void OnUpstreamLogFilterTextChanged(string value)
    {
        UpdateFilteredLogTexts();
    }

     partial void OnSelectedGpuBackendChanged(string value)
    {
        // Map display name back to GpuBackend enum
        var backends = GpuDetectionSettings.GetAvailableBackends();
        var newBackend = backends.FirstOrDefault(b => $"{b.Name} ({b.Detail})" == SelectedGpuBackend);

        if (newBackend.Backend != GpuDetectionService.GpuBackend.CpuOnly)
        {
            GpuDetectionSettings.PreferredBackend = newBackend.Backend;
            OnLogMessage($"[ui] GPU backend set to: {newBackend.Name}");
        }
        else
        {
            GpuDetectionSettings.PreferredBackend = null; // reset to auto-detect
            OnLogMessage("[ui] GPU backend reset to auto-detect (CPU fallback)");
        }

        UpdateCudaVersionVisibility();
    }

    private async Task PollMetricsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _metricsService != null)
        {
            try
            {
                // Re-detect the llama-server port on each poll (fire-and-forget, non-blocking)
                _processManager.RefreshLlamaServerUrl();
                var baseUrl = _processManager.LlamaServerBaseUrl ?? _processManager.DetectedApiBaseUrl;
                if (!string.IsNullOrEmpty(baseUrl) && baseUrl != _metricsService.ApiBaseUrl)
                {
                    _metricsService.SetApiBaseUrl(baseUrl);
                }

                var metrics = await _metricsService.GetMetricsAsync();
                if (metrics != null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        PrefillTokens = (long)metrics.PromptTokens;
                        DecodeTokens = (long)metrics.EvalTokens;
                        ActiveSlots = metrics.ActiveSlots;
                        // Idle fallback only — live TPS comes from upstream print_timing logs.
                        if (TokensPerSecond <= 0 && metrics.TokensPerSecond > 0)
                            TokensPerSecond = metrics.TokensPerSecond;
                    });

                    // Refresh loaded models
                    RefreshLoadedModelsAsync();
                }

                // Retry starting the upstream log stream on each poll cycle.
                // The stream may not start immediately if no model is loaded yet —
                // this ensures it kicks in as soon as a model becomes ready.
                await StartLogStreamingAsync();
            }
            catch { /* ignore polling errors */ }

            await Task.Delay(2000, ct);
        }
    }
}

// Editable model item that parses cmd string
public partial class ModelEditItem : ObservableObject
{
    [ObservableProperty] private string _modelId = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _llamaServerPath = "";
    [ObservableProperty] private string _hfModel = "";
    [ObservableProperty] private string _modelPath = "";
    [ObservableProperty] private string _selectedQuantization = "";
    [ObservableProperty] private string _host = "127.0.0.1";
    [ObservableProperty] private string _port = "${PORT}";
    [ObservableProperty] private string _temperature = "0.2";
    [ObservableProperty] private string _topK = "80";
    [ObservableProperty] private string _repeatPenalty = "1.0";
    [ObservableProperty] private string _presencePenalty = "0";
    [ObservableProperty] private string _contextSize = "";
    [ObservableProperty] private string _predict = "";
    [ObservableProperty] private string _batchSize = "";
    [ObservableProperty] private string _uBatchSize = "";
    [ObservableProperty] private string _threads = "";
    [ObservableProperty] private string _threadsBatch = "";
    [ObservableProperty] private string _gpuLayers = "";
    [ObservableProperty] private string _device = "";
    [ObservableProperty] private string _splitMode = "";
    [ObservableProperty] private string _tensorSplit = "";
    [ObservableProperty] private string _mainGpu = "";
    [ObservableProperty] private string _flashAttention = "";
    [ObservableProperty] private string _cacheTypeK = "";
    [ObservableProperty] private string _cacheTypeV = "";
    [ObservableProperty] private bool _mlock;
    [ObservableProperty] private string _topP = "";
    [ObservableProperty] private string _minP = "";
    [ObservableProperty] private string _frequencyPenalty = "";
    [ObservableProperty] private string _repeatLastN = "";
    [ObservableProperty] private string _seed = "";
    [ObservableProperty] private string _samplers = "";
    [ObservableProperty] private string _parallel = "";
    [ObservableProperty] private bool _contBatching = true;
    [ObservableProperty] private bool _embeddings;
    [ObservableProperty] private bool _reranking;
    [ObservableProperty] private bool _metrics = true;
    [ObservableProperty] private bool _propsEndpoint;
    [ObservableProperty] private bool _slots = true;
    [ObservableProperty] private string _timeout = "";
    [ObservableProperty] private string _threadsHttp = "";
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private string _chatTemplate = "";
    [ObservableProperty] private string _reasoning = "";
    [ObservableProperty] private string _reasoningFormat = "";
    [ObservableProperty] private string _reasoningBudget = "";
    [ObservableProperty] private string _extraArgs = "";
    [ObservableProperty] private bool _useJinja;
    [ObservableProperty] private bool _fitOn;
    [ObservableProperty] private bool _noMmap;
    [ObservableProperty] private string _ttl = "0";
    [ObservableProperty] private string _aliasesText = "";
    [ObservableProperty] private bool _isNew = false;
    [ObservableProperty] private bool _isSelected = false;

    public string CmdPreview
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ModelPath))
                return $"-m {Path.GetFileName(ModelPath)} --temp {Temperature}";
            if (!string.IsNullOrWhiteSpace(HfModel))
            {
                var q = !string.IsNullOrWhiteSpace(SelectedQuantization) ? $" ({SelectedQuantization})" : "";
                return $"--hf {HfModel}{q} --temp {Temperature}";
            }
            return $"--temp {Temperature}";
        }
    }

    public ModelEditItem CloneAs(string newModelId)
    {
        return new ModelEditItem
        {
            ModelId = newModelId,
            Name = string.IsNullOrWhiteSpace(Name) ? newModelId : $"{Name} copy",
            Description = Description,
            AliasesText = "",
            LlamaServerPath = LlamaServerPath,
            ModelPath = ModelPath,
            SelectedQuantization = SelectedQuantization,
            HfModel = HfModel,
            Host = Host,
            Port = Port,
            UseJinja = UseJinja,
            ContextSize = ContextSize,
            Predict = Predict,
            Threads = Threads,
            ThreadsBatch = ThreadsBatch,
            BatchSize = BatchSize,
            UBatchSize = UBatchSize,
            GpuLayers = GpuLayers,
            Device = Device,
            SplitMode = SplitMode,
            TensorSplit = TensorSplit,
            MainGpu = MainGpu,
            FlashAttention = FlashAttention,
            FitOn = FitOn,
            NoMmap = NoMmap,
            Mlock = Mlock,
            CacheTypeK = CacheTypeK,
            CacheTypeV = CacheTypeV,
            Temperature = Temperature,
            TopK = TopK,
            TopP = TopP,
            MinP = MinP,
            RepeatPenalty = RepeatPenalty,
            PresencePenalty = PresencePenalty,
            FrequencyPenalty = FrequencyPenalty,
            RepeatLastN = RepeatLastN,
            Seed = Seed,
            Samplers = Samplers,
            Parallel = Parallel,
            ContBatching = ContBatching,
            Embeddings = Embeddings,
            Reranking = Reranking,
            Metrics = Metrics,
            PropsEndpoint = PropsEndpoint,
            Slots = Slots,
            Timeout = Timeout,
            ThreadsHttp = ThreadsHttp,
            ApiKey = ApiKey,
            ChatTemplate = ChatTemplate,
            Reasoning = Reasoning,
            ReasoningFormat = ReasoningFormat,
            ReasoningBudget = ReasoningBudget,
            ExtraArgs = ExtraArgs,
            Ttl = Ttl,
            IsNew = true
        };
    }

    public static ModelEditItem Parse(string modelId, ModelConfig config)
    {
        var item = new ModelEditItem
        {
            ModelId = modelId,
            Name = config.Name ?? modelId,
            Description = config.Description ?? "",
            UseJinja = config.Cmd?.Contains("--jinja") ?? false,
            FitOn = config.Cmd?.Contains("--fit on") ?? false,
            NoMmap = config.Cmd?.Contains("--no-mmap") ?? false,
            Ttl = config.Ttl?.ToString() ?? "0",
            AliasesText = (config.Aliases ?? new List<string>())
                   .Where(a => !string.IsNullOrEmpty(a))
                   .ToList() is var aliases && aliases.Count > 0
                   ? string.Join(", ", aliases)
                   : ""
        };

        if (!string.IsNullOrEmpty(config.Cmd))
        {
            var parts = TokenizeCommand(config.Cmd);
            if (parts.Count > 0)
                item.LlamaServerPath = parts[0];

            var extras = new List<string>();
            for (var i = 1; i < parts.Count; i++)
            {
                var part = parts[i];
                string? Next() => i + 1 < parts.Count ? Unquote(parts[++i]) : null;
                bool Match(params string[] names) => names.Contains(part);

                if (Match("--jinja")) item.UseJinja = true;
                else if (Match("--no-jinja")) item.UseJinja = false;
                else if (Match("--fit", "-fit")) item.FitOn = (Next() ?? "on") == "on";
                else if (Match("--no-mmap")) item.NoMmap = true;
                else if (Match("--mmap")) item.NoMmap = false;
                else if (Match("--mlock")) item.Mlock = true;
                else if (Match("-m", "--model")) item.ModelPath = Next() ?? "";
                else if (Match("-hf", "-hfr", "--hf", "--hf-repo"))
                {
                    var hfArg = Next() ?? "";
                    var colonIdx = hfArg.LastIndexOf(':');
                    if (colonIdx > 0)
                    {
                        item.HfModel = hfArg[..colonIdx];
                        item.SelectedQuantization = hfArg[(colonIdx + 1)..];
                    }
                    else
                    {
                        item.HfModel = hfArg;
                        item.SelectedQuantization = "";
                    }
                }
                else if (Match("--host")) item.Host = Next() ?? item.Host;
                else if (Match("--port")) item.Port = Next() ?? item.Port;
                else if (Match("--temp", "--temperature")) item.Temperature = Next() ?? item.Temperature;
                else if (Match("--top-k")) item.TopK = Next() ?? item.TopK;
                else if (Match("--top-p")) item.TopP = Next() ?? "";
                else if (Match("--min-p")) item.MinP = Next() ?? "";
                else if (Match("--repeat-penalty")) item.RepeatPenalty = Next() ?? item.RepeatPenalty;
                else if (Match("--presence-penalty")) item.PresencePenalty = Next() ?? item.PresencePenalty;
                else if (Match("--frequency-penalty")) item.FrequencyPenalty = Next() ?? "";
                else if (Match("--repeat-last-n")) item.RepeatLastN = Next() ?? "";
                else if (Match("-s", "--seed")) item.Seed = Next() ?? "";
                else if (Match("--samplers")) item.Samplers = Next() ?? "";
                else if (Match("--ctx-size", "-c")) item.ContextSize = Next() ?? "";
                else if (Match("--predict", "--n-predict", "-n")) item.Predict = Next() ?? "";
                else if (Match("--batch-size", "-b")) item.BatchSize = Next() ?? "";
                else if (Match("--ubatch-size", "-ub")) item.UBatchSize = Next() ?? "";
                else if (Match("--threads", "-t")) item.Threads = Next() ?? "";
                else if (Match("--threads-batch", "-tb")) item.ThreadsBatch = Next() ?? "";
                else if (Match("--gpu-layers", "--n-gpu-layers", "-ngl")) item.GpuLayers = Next() ?? "";
                else if (Match("--device", "-dev")) item.Device = Next() ?? "";
                else if (Match("--split-mode", "-sm")) item.SplitMode = Next() ?? "";
                else if (Match("--tensor-split", "-ts")) item.TensorSplit = Next() ?? "";
                else if (Match("--main-gpu", "-mg")) item.MainGpu = Next() ?? "";
                else if (Match("--flash-attn", "-fa")) item.FlashAttention = Next() ?? "";
                else if (Match("--cache-type-k", "-ctk")) item.CacheTypeK = Next() ?? "";
                else if (Match("--cache-type-v", "-ctv")) item.CacheTypeV = Next() ?? "";
                else if (Match("--parallel", "-np")) item.Parallel = Next() ?? "";
                else if (Match("--cont-batching", "-cb")) item.ContBatching = true;
                else if (Match("--no-cont-batching", "-nocb")) item.ContBatching = false;
                else if (Match("--embedding", "--embeddings")) item.Embeddings = true;
                else if (Match("--rerank", "--reranking")) item.Reranking = true;
                else if (Match("--metrics")) item.Metrics = true;
                else if (Match("--props")) item.PropsEndpoint = true;
                else if (Match("--slots")) item.Slots = true;
                else if (Match("--no-slots")) item.Slots = false;
                else if (Match("--timeout", "-to")) item.Timeout = Next() ?? "";
                else if (Match("--threads-http")) item.ThreadsHttp = Next() ?? "";
                else if (Match("--api-key")) item.ApiKey = Next() ?? "";
                else if (Match("--chat-template")) item.ChatTemplate = Next() ?? "";
                else if (Match("--reasoning", "-rea")) item.Reasoning = Next() ?? "";
                else if (Match("--reasoning-format")) item.ReasoningFormat = Next() ?? "";
                else if (Match("--reasoning-budget")) item.ReasoningBudget = Next() ?? "";
                else extras.Add(part);
            }
            item.ExtraArgs = string.Join(" ", extras);
        }

        return item;
    }

    public string BuildCmd()
    {
        var parts = new List<string>();
        void AddValue(string flag, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) parts.Add($"{flag} {QuoteIfNeeded(value.Trim())}");
        }

        static string? NormalizeOptional(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var t = value.Trim();
            // UI may show "(default)" for omit; never emit that token to the CLI.
            if (t.Equals("(default)", StringComparison.OrdinalIgnoreCase)) return null;
            return t;
        }

        if (!string.IsNullOrWhiteSpace(LlamaServerPath)) parts.Add(QuoteIfNeeded(LlamaServerPath.Trim()));
        if (!string.IsNullOrWhiteSpace(ModelPath)) AddValue("-m", ModelPath);
        else if (!string.IsNullOrWhiteSpace(HfModel))
        {
            var hfArg = SelectedQuantization != null && SelectedQuantization.Contains('/')
                ? HfModel + ":" + SelectedQuantization
                : (string.IsNullOrEmpty(SelectedQuantization) ? HfModel : HfModel + ":" + SelectedQuantization);
            AddValue("-hf", hfArg);
        }

        AddValue("--host", Host);
        AddValue("--port", Port);
        AddValue("--threads", Threads);
        AddValue("--threads-batch", ThreadsBatch);
        AddValue("--ctx-size", ContextSize);
        AddValue("--predict", Predict);
        AddValue("--batch-size", BatchSize);
        AddValue("--ubatch-size", UBatchSize);
        AddValue("--gpu-layers", NormalizeOptional(GpuLayers));
        AddValue("--device", Device);
        AddValue("--split-mode", SplitMode);
        AddValue("--tensor-split", TensorSplit);
        AddValue("--main-gpu", MainGpu);
        AddValue("--flash-attn", FlashAttention);
        AddValue("--cache-type-k", CacheTypeK);
        AddValue("--cache-type-v", CacheTypeV);
        if (Mlock) parts.Add("--mlock");
        if (NoMmap) parts.Add("--no-mmap");
        if (FitOn) parts.Add("--fit on");

        AddValue("--temp", Temperature);
        AddValue("--top-k", TopK);
        AddValue("--top-p", TopP);
        AddValue("--min-p", MinP);
        AddValue("--repeat-penalty", RepeatPenalty);
        AddValue("--presence-penalty", PresencePenalty);
        AddValue("--frequency-penalty", FrequencyPenalty);
        AddValue("--repeat-last-n", RepeatLastN);
        AddValue("--seed", Seed);
        AddValue("--samplers", Samplers);

        AddValue("--parallel", Parallel);
        if (!ContBatching) parts.Add("--no-cont-batching");
        if (Embeddings) parts.Add("--embeddings");
        if (Reranking) parts.Add("--reranking");
        if (Metrics) parts.Add("--metrics");
        if (PropsEndpoint) parts.Add("--props");
        if (!Slots) parts.Add("--no-slots");
        AddValue("--timeout", Timeout);
        AddValue("--threads-http", ThreadsHttp);
        AddValue("--api-key", ApiKey);

        if (UseJinja) parts.Add("--jinja");
        AddValue("--chat-template", NormalizeOptional(ChatTemplate));
        // Critical: "off" must always emit --reasoning off (not omit the flag).
        AddValue("--reasoning", NormalizeOptional(Reasoning));
        AddValue("--reasoning-format", NormalizeOptional(ReasoningFormat));
        AddValue("--reasoning-budget", NormalizeOptional(ReasoningBudget));

        if (!string.IsNullOrWhiteSpace(ExtraArgs)) parts.Add(ExtraArgs.Trim());
        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static List<string> TokenizeCommand(string command)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < command.Length; i++)
        {
            var ch = command[i];
            if (ch == '"' && (i == 0 || command[i - 1] != '\\'))
            {
                inQuotes = !inQuotes;
                current.Append(ch);
                continue;
            }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(ch);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value.StartsWith("\"") && value.EndsWith("\""))
            return value[1..^1].Replace("\\\"", "\"");
        return value;
    }

    private static string QuoteIfNeeded(string value)
    {
        return value.Contains(' ') ? $"\"{value}\"" : value;
    }
}

public partial class MatrixCombinationItem : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private ObservableCollection<MatrixModelSelectionItem> _models = new();
    public ObservableCollection<MatrixCombinationItem>? ParentCollection { get; set; }
    public event Action? Changed;
    public ICommand RemoveCommand { get; }

    public MatrixCombinationItem()
    {
        RemoveCommand = new RelayCommand(() =>
        {
            if (ParentCollection != null && ParentCollection.Contains(this))
            {
                ParentCollection.Remove(this);
                Changed?.Invoke();
            }
        });
    }

    partial void OnNameChanged(string value) => Changed?.Invoke();
}

public partial class MatrixModelSelectionItem : ObservableObject
{
    [ObservableProperty] private string _modelId = "";
    [ObservableProperty] private string _alias = "";
    [ObservableProperty] private bool _isSelected;
    public event Action? Changed;

    partial void OnIsSelectedChanged(bool value) => Changed?.Invoke();
    partial void OnModelIdChanged(string value) => Changed?.Invoke();
    partial void OnAliasChanged(string value) => Changed?.Invoke();
}

// Matrix entry item (reusable for vars and sets)
public partial class MatrixEntryItem : ObservableObject
{
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _value = "";

    public event Action? Changed;
    public ICommand RemoveCommand { get; }
    public ObservableCollection<MatrixEntryItem>? ParentCollection { get; set; }

    public MatrixEntryItem()
    {
        RemoveCommand = new RelayCommand(() =>
        {
            if (ParentCollection != null && ParentCollection.Contains(this))
            {
                ParentCollection.Remove(this);
                Changed?.Invoke();
            }
        });
    }

    partial void OnKeyChanged(string value) => Changed?.Invoke();
    partial void OnValueChanged(string value) => Changed?.Invoke();
}

// Evict cost item
public partial class EvictCostItem : ObservableObject
{
    public IReadOnlyList<string> PriorityOptions { get; } = new[] { "Keep longer", "Normal", "Evict sooner", "Custom" };

    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _modelId = "";
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _priority = "Normal";
    private int _syncDepth = 0;

    /// <summary>
    /// Raised when priority or value changes, to notify the parent collection.
    /// </summary>
    public event Action? Changed;

    public ICommand RemoveCommand { get; }
    public ObservableCollection<EvictCostItem>? ParentCollection { get; set; }

    public EvictCostItem()
    {
        RemoveCommand = new RelayCommand(() =>
        {
            if (ParentCollection != null && ParentCollection.Contains(this))
            {
                ParentCollection.Remove(this);
                Changed?.Invoke();
            }
        });
    }

    partial void OnPriorityChanged(string value)
    {
        if (_syncDepth > 0) return;
        _syncDepth++;
        try
        {
            var nextValue = value switch
            {
                "Keep longer" => "1",
                "Normal" => "",
                "Evict sooner" => "100",
                _ => Value
            };
            if (Value != nextValue) Value = nextValue;
            Changed?.Invoke();
        }
        finally { _syncDepth--; }
    }

    partial void OnValueChanged(string value)
    {
        if (_syncDepth > 0) return;
        _syncDepth++;
        try
        {
            var sanitized = SanitizeCost(value);
            if (Value != sanitized)
            {
                Value = sanitized;
            }

            var next = PriorityFromCost(sanitized);
            // Manual cost edits must update the visual priority without letting
            // OnPriorityChanged map the combo selection back over the typed value.
            // Example: typing 60 should show Custom, but must keep Value=60.
            if (Priority != next)
            {
                Priority = next;
            }
            Changed?.Invoke();
        }
        finally { _syncDepth--; }
    }

    partial void OnKeyChanged(string value) => Changed?.Invoke();
    partial void OnModelIdChanged(string value) => Changed?.Invoke();

    private static string SanitizeCost(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (string.IsNullOrWhiteSpace(digits)) return string.Empty;
        if (!int.TryParse(digits, out var cost)) return string.Empty;
        return cost <= 0 ? string.Empty : cost.ToString();
    }

    public static string PriorityFromCost(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Normal";
        if (!int.TryParse(value, out var cost)) return "Custom";
        return cost switch
        {
            <= 0 => "Normal",
            1 => "Keep longer",
            100 => "Evict sooner",
            _ => "Custom"
        };
    }
}
