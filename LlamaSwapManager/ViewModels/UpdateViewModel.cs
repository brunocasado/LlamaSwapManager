using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaSwapManager.Services;

namespace LlamaSwapManager.ViewModels;

/// <summary>
/// ViewModel for the Updates tab.
/// Handles checking for and installing llama-swap updates.
/// </summary>
public partial class UpdateViewModel : ObservableObject, IDisposable
{
    private readonly UpdateService _updateService;
    private readonly LlamaDeckUpdateService _llamaDeckUpdateService;
    private readonly LlamaCppDownloader _llamaCppDownloader;
    private readonly Action<string>? _logMessage;
    private readonly string? _preferredCudaVersion;
    private readonly Func<Task>? _requestApplicationExit;
    private LlamaDeckUpdateInfo? _llamaDeckUpdate;
    private CancellationTokenSource? _updateCts;
    private bool _disposed;

    // LlamaDeck version info
    [ObservableProperty] private string _llamaDeckCurrentVersion = "Unknown";
    [ObservableProperty] private string _llamaDeckLatestVersion = "";
    [ObservableProperty] private bool _isLlamaDeckUpdateAvailable;
    [ObservableProperty] private string _llamaDeckStatusText = "";
    [ObservableProperty] private string _llamaDeckStatusColor = "#888888";
    [ObservableProperty] private bool _llamaDeckCheckButtonEnabled = true;
    [ObservableProperty] private bool _llamaDeckUpdateButtonEnabled;

    // llama-swap version info
    [ObservableProperty] private string _currentVersion = "Unknown";
    [ObservableProperty] private string _latestVersion = "";
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private string _updateStatusText = "";
    [ObservableProperty] private string _updateStatusColor = "#888888";

    // llama.cpp version info
    [ObservableProperty] private string _llamaCppCurrentVersion = "Unknown";
    [ObservableProperty] private string _llamaCppLatestVersion = "";
    [ObservableProperty] private bool _isLlamaCppUpdateAvailable;
    [ObservableProperty] private string _llamaCppStatusText = "";
    [ObservableProperty] private string _llamaCppStatusColor = "#888888";

    // Progress
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private int _progressPercentage;
    [ObservableProperty] private bool _isUpdating;

    // Actions
    [ObservableProperty] private bool _checkButtonEnabled = true;
    [ObservableProperty] private bool _updateButtonEnabled = false;
    [ObservableProperty] private bool _llamaCppCheckButtonEnabled = true;
    [ObservableProperty] private bool _llamaCppUpdateButtonEnabled = false;

    // Install directory info
    public string InstallDirectory { get; }
    public string LlamaCppDirectory { get; }

    public ICommand LlamaDeckCheckCommand { get; }
    public ICommand LlamaDeckUpdateCommand { get; }
    public ICommand OpenLlamaDeckReleaseCommand { get; }
    public ICommand OpenLlamaSwapRepositoryCommand { get; }
    public ICommand OpenLlamaCppRepositoryCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand LlamaCppCheckCommand { get; }
    public ICommand LlamaCppUpdateCommand { get; }

    public UpdateViewModel(
        string installDirectory,
        string? llamaCppDirectory = null,
        Action<string>? logMessage = null,
        string? preferredCudaVersion = null,
        Func<Task>? requestApplicationExit = null)
    {
        InstallDirectory = installDirectory;
        LlamaCppDirectory = llamaCppDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llama");
        _logMessage = logMessage;
        _updateService = new UpdateService(installDirectory);
        _llamaDeckUpdateService = new LlamaDeckUpdateService();
        _llamaCppDownloader = new LlamaCppDownloader();
        _preferredCudaVersion = preferredCudaVersion;
        _requestApplicationExit = requestApplicationExit;

        _updateService.ProgressChanged += OnProgressChanged;
        _updateService.LogMessage += OnLogMessage;
        _llamaCppDownloader.LogMessage += OnLogMessage;

        LlamaDeckCheckCommand = new AsyncRelayCommand(CheckLlamaDeckUpdatesInternalAsync);
        LlamaDeckUpdateCommand = new AsyncRelayCommand(ExecuteLlamaDeckUpdateAsync);
        OpenLlamaDeckReleaseCommand = new RelayCommand(OpenLlamaDeckRelease);
        OpenLlamaSwapRepositoryCommand = new RelayCommand(
            () => OpenUrl("https://github.com/mostlygeek/llama-swap"));
        OpenLlamaCppRepositoryCommand = new RelayCommand(
            () => OpenUrl("https://github.com/ggml-org/llama.cpp"));
        CheckCommand = new AsyncRelayCommand(CheckForUpdatesInternalAsync);
        UpdateCommand = new AsyncRelayCommand(ExecuteUpdateAsync);
        LlamaCppCheckCommand = new AsyncRelayCommand(CheckLlamaCppUpdatesInternalAsync);
        LlamaCppUpdateCommand = new AsyncRelayCommand(ExecuteLlamaCppUpdateAsync);

        UpdateButtonEnabled = false;
        _ = CheckLlamaDeckUpdatesInternalAsync();
        _ = CheckForUpdatesInternalAsync();
        _ = CheckLlamaCppUpdatesInternalAsync();
    }

    private void OpenLlamaDeckRelease()
    {
        var url = _llamaDeckUpdate?.ReleaseUrl;
        OpenUrl(string.IsNullOrWhiteSpace(url)
            ? "https://github.com/brunocasado/LlamaDeck/releases/latest"
            : url);
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            OnLogMessage($"Could not open URL: {ex.Message}");
        }
    }

    private async Task CheckLlamaDeckUpdatesInternalAsync()
    {
        if (IsUpdating) return;

        LlamaDeckCheckButtonEnabled = false;
        LlamaDeckStatusText = "Checking for updates...";
        LlamaDeckStatusColor = "#89B4FA";

        try
        {
            _llamaDeckUpdate = await _llamaDeckUpdateService.CheckAsync();
            if (_llamaDeckUpdate is null)
            {
                LlamaDeckStatusText = "Could not check for updates";
                LlamaDeckStatusColor = "#F38BA8";
                IsLlamaDeckUpdateAvailable = false;
                LlamaDeckUpdateButtonEnabled = false;
                return;
            }

            LlamaDeckCurrentVersion = _llamaDeckUpdate.CurrentVersion;
            LlamaDeckLatestVersion = _llamaDeckUpdate.LatestVersion;
            IsLlamaDeckUpdateAvailable = _llamaDeckUpdate.IsUpdateAvailable;

            if (!_llamaDeckUpdate.IsUpdateAvailable)
            {
                LlamaDeckStatusText = "Already up to date";
                LlamaDeckStatusColor = "#A6E3A1";
                LlamaDeckUpdateButtonEnabled = false;
                return;
            }

            if (string.IsNullOrWhiteSpace(_llamaDeckUpdate.AssetUrl))
            {
                LlamaDeckStatusText = "Update available, but no compatible package was found";
                LlamaDeckStatusColor = "#F9E2AF";
                LlamaDeckUpdateButtonEnabled = false;
                return;
            }

            if (!_llamaDeckUpdateService.CanInstallUpdate)
            {
                LlamaDeckStatusText = $"Update {_llamaDeckUpdate.LatestVersion} available. Install it from GitHub Releases.";
                LlamaDeckStatusColor = "#F9E2AF";
                LlamaDeckUpdateButtonEnabled = false;
                return;
            }

            LlamaDeckStatusText = $"Update {_llamaDeckUpdate.LatestVersion} available";
            LlamaDeckStatusColor = "#F9E2AF";
            LlamaDeckUpdateButtonEnabled = true;
        }
        catch (Exception ex)
        {
            LlamaDeckStatusText = $"Error: {ex.Message}";
            LlamaDeckStatusColor = "#F38BA8";
            IsLlamaDeckUpdateAvailable = false;
            LlamaDeckUpdateButtonEnabled = false;
            OnLogMessage($"LlamaDeck update check failed: {ex.Message}");
        }
        finally
        {
            LlamaDeckCheckButtonEnabled = true;
        }
    }

    private async Task ExecuteLlamaDeckUpdateAsync()
    {
        if (IsUpdating) return;

        if (_llamaDeckUpdate is null || !_llamaDeckUpdate.IsUpdateAvailable)
        {
            await CheckLlamaDeckUpdatesInternalAsync();
            if (_llamaDeckUpdate is null || !_llamaDeckUpdate.IsUpdateAvailable)
                return;
        }

        IsUpdating = true;
        LlamaDeckCheckButtonEnabled = false;
        LlamaDeckUpdateButtonEnabled = false;
        ProgressText = "Starting LlamaDeck update...";
        ProgressPercentage = 0;
        _updateCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<(string Message, int Percentage)>(value =>
            {
                ProgressText = value.Message;
                ProgressPercentage = value.Percentage;
            });
            var prepared = await _llamaDeckUpdateService.DownloadAndPrepareAsync(
                _llamaDeckUpdate,
                progress,
                _updateCts.Token);

            if (!prepared)
            {
                LlamaDeckStatusText = "Update could not be prepared";
                LlamaDeckStatusColor = "#F38BA8";
                LlamaDeckUpdateButtonEnabled = true;
                return;
            }

            LlamaDeckStatusText = "Update downloaded. Restarting LlamaDeck...";
            LlamaDeckStatusColor = "#A6E3A1";
            OnLogMessage($"LlamaDeck {_llamaDeckUpdate.LatestVersion} is ready to install");

            if (_requestApplicationExit is not null)
                await _requestApplicationExit();
            else
                LlamaDeckStatusText = "Update ready. Close LlamaDeck to finish installation.";
        }
        catch (OperationCanceledException)
        {
            LlamaDeckStatusText = "Update cancelled";
            LlamaDeckStatusColor = "#F9E2AF";
            LlamaDeckUpdateButtonEnabled = true;
        }
        catch (Exception ex)
        {
            LlamaDeckStatusText = $"Error: {ex.Message}";
            LlamaDeckStatusColor = "#F38BA8";
            LlamaDeckUpdateButtonEnabled = true;
            OnLogMessage($"LlamaDeck update failed: {ex.Message}");
        }
        finally
        {
            IsUpdating = false;
            LlamaDeckCheckButtonEnabled = true;
            _updateCts?.Dispose();
            _updateCts = null;
        }
    }

    private async Task CheckForUpdatesInternalAsync()
    {
        if (IsUpdating) return;

        CheckButtonEnabled = false;
        UpdateStatusText = "Checking for updates...";
        UpdateStatusColor = "#89B4FA";

        try
        {
            var latest = await _updateService.CheckForUpdatesAsync();

            if (latest == null)
            {
                UpdateStatusText = "Could not check for updates";
                UpdateStatusColor = "#F38BA8";
                IsUpdateAvailable = false;
                UpdateButtonEnabled = false;
                return;
            }

            LatestVersion = latest.Version ?? "Unknown";
            CurrentVersion = await DetectCurrentVersionAsync();

            // Fail closed when local version cannot be detected/parsed — do NOT treat Unknown as outdated.
            if (string.IsNullOrWhiteSpace(CurrentVersion)
                || CurrentVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                || VersionComparer.Parse(CurrentVersion) is null)
            {
                UpdateStatusText = "Could not detect installed version";
                UpdateStatusColor = "#F38BA8";
                IsUpdateAvailable = false;
                UpdateButtonEnabled = false;
                _logMessage?.Invoke($"Could not detect installed llama-swap version (got: {CurrentVersion}). Install dir: {InstallDirectory}");
                return;
            }

            // Use the shared VersionComparer for comparison
            var hasUpdate = VersionComparer.HasUpdate(CurrentVersion, LatestVersion);

            if (hasUpdate)
            {
                IsUpdateAvailable = true;
                UpdateStatusText = $"Update {LatestVersion} available";
                UpdateStatusColor = "#F9E2AF";
                UpdateButtonEnabled = true;
                _logMessage?.Invoke($"Update available: {LatestVersion} (current: {CurrentVersion})");
            }
            else
            {
                IsUpdateAvailable = false;
                UpdateStatusText = "Already up to date";
                UpdateStatusColor = "#A6E3A1";
                UpdateButtonEnabled = false;
                _logMessage?.Invoke("llama-swap is up to date");
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"Error: {ex.Message}";
            UpdateStatusColor = "#F38BA8";
            IsUpdateAvailable = false;
            UpdateButtonEnabled = false;
        }
        finally
        {
            CheckButtonEnabled = true;
        }
    }

    private async Task<string> DetectCurrentVersionAsync()
    {
        try
        {
            var exePath = Path.Combine(InstallDirectory, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "llama-swap.exe" : "llama-swap");
            if (!File.Exists(exePath)) return "Unknown";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return "Unknown";

            // Read BOTH streams (version may be on stderr) and avoid pipeline deadlock.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var output = (await stdoutTask) + (await stderrTask);

            // Build prints "version: v233 (...)" (with leading v) or historically "version: 223 (...)".
            var match = System.Text.RegularExpressions.Regex.Match(
                output, @"version:\s*v?(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
                return $"v{match.Groups[1].Value}";

            return "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private async Task CheckLlamaCppUpdatesInternalAsync()
    {
        if (IsUpdating) return;

        LlamaCppCheckButtonEnabled = false;
        LlamaCppStatusText = "Checking for updates...";
        LlamaCppStatusColor = "#89B4FA";

        try
        {
            var result = await _llamaCppDownloader.CheckForUpdateAsync(LlamaCppDirectory);

            if (result.RemoteVersion == null)
            {
                LlamaCppStatusText = "Could not check for updates";
                LlamaCppStatusColor = "#F38BA8";
                IsLlamaCppUpdateAvailable = false;
                LlamaCppUpdateButtonEnabled = false;
                return;
            }

            LlamaCppLatestVersion = result.RemoteVersion;
            LlamaCppCurrentVersion = result.LocalVersion ?? "Unknown";

            if (result.HasUpdate)
            {
                IsLlamaCppUpdateAvailable = true;
                LlamaCppStatusText = $"Update {result.RemoteVersion} available";
                LlamaCppStatusColor = "#F9E2AF";
                LlamaCppUpdateButtonEnabled = true;
                _logMessage?.Invoke($"llama.cpp update available: {result.RemoteVersion} (current: {result.LocalVersion})");
            }
            else
            {
                IsLlamaCppUpdateAvailable = false;
                LlamaCppStatusText = "Already up to date";
                LlamaCppStatusColor = "#A6E3A1";
                LlamaCppUpdateButtonEnabled = false;
                _logMessage?.Invoke("llama.cpp is up to date");
            }
        }
        catch (Exception ex)
        {
            LlamaCppStatusText = $"Error: {ex.Message}";
            LlamaCppStatusColor = "#F38BA8";
            IsLlamaCppUpdateAvailable = false;
            LlamaCppUpdateButtonEnabled = false;
        }
        finally
        {
            LlamaCppCheckButtonEnabled = true;
        }
    }

    private async Task ExecuteLlamaCppUpdateAsync()
    {
        if (IsUpdating) return;

        IsUpdating = true;
        LlamaCppCheckButtonEnabled = false;
        LlamaCppUpdateButtonEnabled = false;
        ProgressText = "Starting llama.cpp update...";
        ProgressPercentage = 0;

        _updateCts = new CancellationTokenSource();

        try
        {
            var success = await _llamaCppDownloader.DownloadAndInstallAsync(
                LlamaCppDirectory,
                new Progress<double>(p => ProgressPercentage = (int)(p * 100)),
                _updateCts.Token,
                _preferredCudaVersion);

            if (success)
            {
                LlamaCppStatusText = "llama.cpp updated successfully! Restart llama-swap to apply.";
                LlamaCppStatusColor = "#A6E3A1";
                LlamaCppUpdateButtonEnabled = false;
                _logMessage?.Invoke("llama.cpp updated successfully. Restart llama-swap to apply.");

                // Refresh version display — detect the local version after install
                var result = await _llamaCppDownloader.CheckForUpdateAsync(LlamaCppDirectory);
                LlamaCppCurrentVersion = result.LocalVersion ?? "Unknown";
            }
            else
            {
                LlamaCppStatusText = "llama.cpp update failed. Check logs for details.";
                LlamaCppStatusColor = "#F38BA8";
                LlamaCppUpdateButtonEnabled = true;
                _logMessage?.Invoke("llama.cpp update failed");
            }
        }
        catch (Exception ex)
        {
            LlamaCppStatusText = $"Error: {ex.Message}";
            LlamaCppStatusColor = "#F38BA8";
            LlamaCppUpdateButtonEnabled = true;
            _logMessage?.Invoke($"llama.cpp update error: {ex.Message}");
        }
        finally
        {
            IsUpdating = false;
            LlamaCppCheckButtonEnabled = true;
            _updateCts?.Dispose();
            _updateCts = null;
        }
    }

    private async Task ExecuteUpdateAsync()
    {
        if (IsUpdating) return;

        IsUpdating = true;
        CheckButtonEnabled = false;
        UpdateButtonEnabled = false;
        ProgressText = "Starting update...";
        ProgressPercentage = 0;

        _updateCts = new CancellationTokenSource();

        try
        {
            var success = await _updateService.UpdateAsync("", _updateCts.Token);

            if (success)
            {
                UpdateStatusText = "Update successful! Restart llama-swap to apply.";
                UpdateStatusColor = "#A6E3A1";
                _logMessage?.Invoke("llama-swap updated successfully. Restart to apply.");

                // Refresh version display
                CurrentVersion = await DetectCurrentVersionAsync();
            }
            else
            {
                UpdateStatusText = "Update failed. Check logs for details.";
                UpdateStatusColor = "#F38BA8";
                _logMessage?.Invoke("llama-swap update failed");
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"Error: {ex.Message}";
            UpdateStatusColor = "#F38BA8";
            _logMessage?.Invoke($"Update error: {ex.Message}");
        }
        finally
        {
            IsUpdating = false;
            CheckButtonEnabled = true;
            _updateCts?.Dispose();
            _updateCts = null;
        }
    }

    private void OnProgressChanged(UpdateService.UpdateProgress progress)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnProgressChanged(progress));
            return;
        }

        ProgressText = progress.Message;
        ProgressPercentage = progress.Percentage;
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

        _logMessage?.Invoke(message);
    }

    public void CancelUpdate()
    {
        _updateCts?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _updateCts?.Dispose();
        _updateService?.Dispose();
        _llamaDeckUpdateService.Dispose();
        _llamaCppDownloader?.Dispose();
    }
}
