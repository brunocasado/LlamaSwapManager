using System;
using System.Runtime.InteropServices;

namespace LlamaSwapManager.Desktop;

internal static class PlatformDetection
{
    private static readonly Lazy<bool> _isWindows = new(() => Environment.OSVersion.Platform == PlatformID.Win32NT);
    private static readonly Lazy<bool> _isMacOS = new(() => RuntimeInformation.IsOSPlatform(OSPlatform.OSX));
    private static readonly Lazy<bool> _isLinux = new(() => RuntimeInformation.IsOSPlatform(OSPlatform.Linux));

    public static bool IsWindows => _isWindows.Value;
    public static bool IsMacOS => _isMacOS.Value;
    public static bool IsLinux => _isLinux.Value;
}
