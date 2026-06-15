using System;
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
    private readonly LlamaCppDownloader _llamaCppDownloader;
    private readonly Action<string>? _logMessage;
    private readonly string? _preferredCudaVersion;
    private CancellationTokenSource? _updateCts;
    private bool _disposed;

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

    public ICommand CheckCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand LlamaCppCheckCommand { get; }
    public ICommand LlamaCppUpdateCommand { get; }

    public UpdateViewModel(string installDirectory, string? llamaCppDirectory = null, Action<string>? logMessage = null, string? preferredCudaVersion = null)
    {
        InstallDirectory = installDirectory;
        LlamaCppDirectory = llamaCppDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llama");
        _logMessage = logMessage;
        _updateService = new UpdateService(installDirectory);
        _llamaCppDownloader = new LlamaCppDownloader();
        _preferredCudaVersion = preferredCudaVersion;

        _updateService.ProgressChanged += OnProgressChanged;
        _updateService.LogMessage += OnLogMessage;
        _llamaCppDownloader.LogMessage += OnLogMessage;

        CheckCommand = new AsyncRelayCommand(CheckForUpdatesInternalAsync);
        UpdateCommand = new AsyncRelayCommand(ExecuteUpdateAsync);
        LlamaCppCheckCommand = new AsyncRelayCommand(CheckLlamaCppUpdatesInternalAsync);
        LlamaCppUpdateCommand = new AsyncRelayCommand(ExecuteLlamaCppUpdateAsync);

        UpdateButtonEnabled = false;
        _ = CheckForUpdatesInternalAsync();
        _ = CheckLlamaCppUpdatesInternalAsync();
    }

    private async Task CheckForUpdatesInternalAsync()
    {
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

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            // Try to extract version number from output like "llama-swap v224" or "v224"
            var match = System.Text.RegularExpressions.Regex.Match(output, @"v?(\d+)");
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

                // Refresh version display
                var result = await _llamaCppDownloader.CheckForUpdateAsync(LlamaCppDirectory);
                LlamaCppCurrentVersion = result.RemoteVersion ?? "Unknown";
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
        _llamaCppDownloader?.Dispose();
    }
}
