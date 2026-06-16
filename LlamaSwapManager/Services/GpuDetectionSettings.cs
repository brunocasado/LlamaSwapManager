using System;
using System.Linq;

namespace LlamaSwapManager.Services;

/// <summary>
/// Shared settings for GPU backend selection.
/// Users can override auto-detection here.
/// </summary>
public static class GpuDetectionSettings
{
    private static GpuDetectionService.GpuBackend? _preferredBackend;

    /// <summary>
    /// User's preferred GPU backend (null = auto-detect).
    /// </summary>
    public static GpuDetectionService.GpuBackend? PreferredBackend
    {
        get => _preferredBackend;
        set => _preferredBackend = value;
    }

    /// <summary>
    /// Get the effective backend (user preference or auto-detect).
    /// </summary>
    public static GpuDetectionService.GpuBackend GetEffectiveBackend()
    {
        if (_preferredBackend.HasValue)
            return _preferredBackend.Value;

        var detected = GpuDetectionService.DetectBackends();
        return detected.Count > 0 ? detected[0].Backend : GpuDetectionService.GpuBackend.CpuOnly;
    }

    /// <summary>
    /// Get the list of available backends for the current platform.
    /// </summary>
    public static GpuDetectionService.GpuBackendInfo[] GetAvailableBackends()
    {
        return GpuDetectionService.DetectBackends().ToArray();
    }
}
