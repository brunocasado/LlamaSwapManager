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
}
