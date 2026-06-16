using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaSwapManager.Services;

/// <summary>
/// Queries GitHub API for latest releases and compares versions
/// between remote and local installations.
/// </summary>
public class VersionChecker : IDisposable
{
    private readonly HttpClient _http;
    private const string GitHubApiBase = "https://api.github.com";
    private const int MaxRetryAttempts = 1;
    private readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(15);

    public VersionChecker(HttpClient? http = null)
    {
        _http = http ?? new HttpClient
        {
            Timeout = _requestTimeout,
            DefaultRequestHeaders =
            {
                { "Accept", "application/vnd.github.v3+json" },
                { "User-Agent", "LlamaSwapManager" }
            }
        };
    }

    #region Public API

    /// <summary>
    /// Check for updates on both llama-swap and llama.cpp.
    /// Network errors are handled gracefully — individual failures
    /// return Unknown/Error status without crashing the caller.
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var result = new UpdateCheckResult();

        // llama-swap check
        try
        {
            var remote = await GetLatestLlamaSwapAsync(ct).ConfigureAwait(false);
            var local = GetInstalledLlamaSwapVersion();
            result.LlamaSwap = Compare(remote, local);
        }
        catch (Exception ex)
        {
            result.LlamaSwap = VersionComparison.ErrorResult(ex.Message);
        }

        // llama.cpp check
        try
        {
            var remote = await GetLatestLlamaCppAsync(ct).ConfigureAwait(false);
            var local = GetInstalledLlamaCppVersion();
            result.LlamaCpp = Compare(remote, local);
        }
        catch (Exception ex)
        {
            result.LlamaCpp = VersionComparison.ErrorResult(ex.Message);
        }

        return result;
    }

    /// <summary>
    /// Check for updates on a single binary type.
    /// </summary>
    public async Task<VersionComparison> CheckBinaryAsync(BinaryType binaryType, CancellationToken ct = default)
    {
        try
        {
            RemoteVersion remote;
            LocalVersion local;

            switch (binaryType)
            {
                case BinaryType.LlamaSwap:
                    remote = await GetLatestLlamaSwapAsync(ct).ConfigureAwait(false);
                    local = GetInstalledLlamaSwapVersion();
                    break;
                case BinaryType.LlamaCpp:
                    remote = await GetLatestLlamaCppAsync(ct).ConfigureAwait(false);
                    local = GetInstalledLlamaCppVersion();
                    break;
                default:
                    return VersionComparison.ErrorResult($"Unknown binary type: {binaryType}");
            }

            return Compare(remote, local);
        }
        catch (Exception ex)
        {
            return VersionComparison.ErrorResult(ex.Message);
        }
    }

    /// <summary>
    /// Parse a llama-swap version string (e.g., "v224") into its numeric value.
    /// </summary>
    public static ulong ParseLlamaSwapVersion(string version)
    {
        if (string.IsNullOrEmpty(version))
            throw new ArgumentException("Version string cannot be null or empty", nameof(version));

        // Format: "v" + integer (e.g., "v224")
        if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            var numStr = version.Substring(1);
            if (ulong.TryParse(numStr, out var num))
                return num;
        }

        throw new FormatException($"Cannot parse llama-swap version: {version} (expected format: v<number>)");
    }

    /// <summary>
    /// Parse a llama.cpp version string (e.g., "b9611") into its numeric value.
    /// </summary>
    public static ulong ParseLlamaCppVersion(string version)
    {
        if (string.IsNullOrEmpty(version))
            throw new ArgumentException("Version string cannot be null or empty", nameof(version));

        // Format: "b" + 4 or 5 hex digits (e.g., "b961" or "b9611")
        if (version.StartsWith("b", StringComparison.OrdinalIgnoreCase) && version.Length >= 5)
        {
            var hexStr = version.Substring(1);
            if (ulong.TryParse(hexStr, System.Globalization.NumberStyles.HexNumber, null, out var num))
                return num;
        }

        throw new FormatException($"Cannot parse llama.cpp version: {version} (expected format: b<hex>)");
    }

    #endregion

    #region GitHub API Queries

    /// <summary>
    /// Query GitHub API for the latest llama-swap release.
    /// </summary>
    private async Task<RemoteVersion> GetLatestLlamaSwapAsync(CancellationToken ct)
    {
        return await FetchLatestReleaseAsync("mostlygeek/llama-swap", BinaryType.LlamaSwap, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Query GitHub API for the latest llama.cpp release.
    /// </summary>
    private async Task<RemoteVersion> GetLatestLlamaCppAsync(CancellationToken ct)
    {
        return await FetchLatestReleaseAsync("ggml-org/llama.cpp", BinaryType.LlamaCpp, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Generic GitHub release fetch with retry and error handling.
    /// </summary>
    private async Task<RemoteVersion> FetchLatestReleaseAsync(string repo, BinaryType binaryType, CancellationToken ct)
    {
        var url = $"{GitHubApiBase}/repos/{repo}/releases/latest";
        RemoteVersion? lastError = null;

        for (int attempt = 0; attempt <= MaxRetryAttempts; attempt++)
        {
            try
            {
                var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    // Don't retry on 4xx client errors (e.g., 404, 403 rate limit)
                    if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                    {
                        return new RemoteVersion
                        {
                            BinaryType = binaryType,
                            Status = UpdateStatus.Error,
                            Error = $"GitHub API returned {(int)response.StatusCode} {response.StatusCode}"
                        };
                    }

                    // Retry on 5xx server errors
                    lastError = new RemoteVersion
                    {
                        BinaryType = binaryType,
                        Status = UpdateStatus.Error,
                        Error = $"GitHub API returned {(int)response.StatusCode} {response.StatusCode}"
                    };
                    if (attempt < MaxRetryAttempts)
                        await Task.Delay(1000 * (attempt + 1), ct).ConfigureAwait(false);
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var tagName = root.GetProperty("tag_name").GetString();
                var downloadUrl = root.TryGetProperty("assets", out var assets)
                    && assets.GetArrayLength() > 0
                    ? assets[0].GetProperty("browser_download_url").GetString()
                    : null;
                var checksum = root.TryGetProperty("assets", out var assets2) && assets2.GetArrayLength() > 0
                    && assets2[0].TryGetProperty("digest", out var digest)
                    ? digest.GetString()
                    : null;

                var remote = new RemoteVersion
                {
                    BinaryType = binaryType,
                    TagName = tagName,
                    DownloadUrl = downloadUrl,
                    Checksum = checksum
                };

                // Parse numeric value based on binary type
                if (tagName is not null)
                {
                    try
                    {
                        remote.NumericValue = binaryType == BinaryType.LlamaSwap
                            ? ParseLlamaSwapVersion(tagName)
                            : ParseLlamaCppVersion(tagName);
                    }
                    catch (FormatException)
                    {
                        // Tag exists but doesn't match expected format — still return it
                        // so the caller can handle it gracefully
                    }
                }

                return remote;
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Don't swallow cancellation
            }
            catch (TaskCanceledException)
            {
                // Timeout — retry once
                lastError = new RemoteVersion
                {
                    BinaryType = binaryType,
                    Status = UpdateStatus.Error,
                    Error = "Request timed out"
                };
                if (attempt < MaxRetryAttempts)
                    await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                lastError = new RemoteVersion
                {
                    BinaryType = binaryType,
                    Status = UpdateStatus.Error,
                    Error = $"Network error: {ex.Message}"
                };
                if (attempt < MaxRetryAttempts)
                    await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }

        return lastError ?? new RemoteVersion
        {
            BinaryType = binaryType,
            Status = UpdateStatus.Error,
            Error = "Failed to fetch release after retries"
        };
    }

    #endregion

    #region Local Version Detection

    /// <summary>
    /// Detect the installed llama-swap version.
    /// Tries: `llama-swap --version`, then `GET /api/version` on running proxy.
    /// </summary>
    private LocalVersion GetInstalledLlamaSwapVersion()
    {
        // Try CLI version flag first
        var cliVersion = TryDetectVersionViaCli("llama-swap", new[] { "--version", "-v" });
        if (cliVersion is not null)
            return cliVersion;

        // Try running API
        return TryDetectVersionViaApi();
    }

    /// <summary>
    /// Detect the installed llama.cpp version.
    /// Tries: `llama-server --help` output parsing, or stored marker file.
    /// </summary>
    private LocalVersion GetInstalledLlamaCppVersion()
    {
        // Try llama-server --help
        var helpVersion = TryDetectVersionFromHelp();
        if (helpVersion is not null)
            return helpVersion;

        // Try marker file in ~/.llama/
        return TryDetectVersionFromMarkerFile();
    }

    /// <summary>
    /// Try to detect version by running a CLI command.
    /// </summary>
    private LocalVersion? TryDetectVersionViaCli(string command, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = string.Join(" ", args),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            proc.WaitForExit(5000);
            var output = proc.StandardOutput.ReadToEnd().Trim();

            if (string.IsNullOrEmpty(output))
                return null;

            // Try parsing as llama-swap version
            try
            {
                return new LocalVersion
                {
                    BinaryType = BinaryType.LlamaSwap,
                    VersionString = output,
                    NumericValue = ParseLlamaSwapVersion(output)
                };
            }
            catch
            {
                // Not a llama-swap version, try llama.cpp
                try
                {
                    return new LocalVersion
                    {
                        BinaryType = BinaryType.LlamaCpp,
                        VersionString = output,
                        NumericValue = ParseLlamaCppVersion(output)
                    };
                }
                catch
                {
                    return null;
                }
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Try to detect llama-swap version from the running API.
    /// </summary>
    private LocalVersion TryDetectVersionViaApi()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = http.GetAsync("http://127.0.0.1:5800/api/version").GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return new LocalVersion { BinaryType = BinaryType.LlamaSwap, Status = UpdateStatus.Unknown };

            var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var version = doc.RootElement.GetProperty("version").GetString();
            if (version is not null)
            {
                return new LocalVersion
                {
                    BinaryType = BinaryType.LlamaSwap,
                    VersionString = version,
                    NumericValue = ParseLlamaSwapVersion(version)
                };
            }
        }
        catch { }

        return new LocalVersion { BinaryType = BinaryType.LlamaSwap, Status = UpdateStatus.Unknown };
    }

    /// <summary>
    /// Try to detect llama.cpp version from llama-server --help output.
    /// </summary>
    private LocalVersion? TryDetectVersionFromHelp()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "llama-server",
                Arguments = "--help",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            proc.WaitForExit(5000);
            var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();

            // llama.cpp --help typically contains the version in the usage line
            // e.g., "llama_server (version: b9611)" or similar
            var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                // Try to find version pattern in the line
                var match = System.Text.RegularExpressions.Regex.Match(line, @"b([0-9a-fA-F]{5})");
                if (match.Success)
                {
                    var version = "b" + match.Groups[1].Value.ToLower();
                    return new LocalVersion
                    {
                        BinaryType = BinaryType.LlamaCpp,
                        VersionString = version,
                        NumericValue = ParseLlamaCppVersion(version)
                    };
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Try to detect llama.cpp version from a marker file in ~/.llama/.
    /// </summary>
    private LocalVersion? TryDetectVersionFromMarkerFile()
    {
        try
        {
            var markerPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".llama", ".version");

            if (!File.Exists(markerPath))
                return null;

            var version = File.ReadAllText(markerPath).Trim();
            if (string.IsNullOrEmpty(version))
                return null;

            return new LocalVersion
            {
                BinaryType = BinaryType.LlamaCpp,
                VersionString = version,
                NumericValue = ParseLlamaCppVersion(version)
            };
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Version Comparison

    /// <summary>
    /// Compare a remote version against a local version.
    /// </summary>
    public VersionComparison Compare(RemoteVersion remote, LocalVersion local)
    {
        // If remote version is unknown, we can't determine update status
        if (!remote.IsKnown)
        {
            return remote.Status == UpdateStatus.Error
                ? VersionComparison.ErrorResult(remote.Error ?? "Failed to fetch remote version")
                : VersionComparison.Unknown("Remote version could not be determined");
        }

        // If local version is unknown, we can't compare
        if (!local.IsKnown)
        {
            return VersionComparison.Unknown(
                local.BinaryType == BinaryType.LlamaSwap
                    ? "Could not detect installed llama-swap version"
                    : "Could not detect installed llama.cpp version");
        }

        // Compare numeric values
        var hasUpdate = remote.NumericValue!.Value > local.NumericValue!.Value;

        return VersionComparison.Success(
            hasUpdate,
            local.VersionString,
            remote.TagName,
            remote.DownloadUrl);
    }

    #endregion

    public void Dispose()
    {
        _http?.Dispose();
    }
}
