using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PSMS.App.Services;

public sealed class AppUpdateInfo
{
    public required string Version { get; init; }
    public required string SetupUrl { get; init; }
    public string? ReleaseNotesUrl { get; init; }
}

/// <summary>
/// Checks the GitHub <c>latest</c> release and can download/launch the Windows Setup EXE.
/// </summary>
public sealed class AppUpdateService : IDisposable
{
    private const string Owner = "PatTheLad";
    private const string Repo = "PSMS";
    private const string VersionJsonUrl =
        "https://github.com/PatTheLad/PSMS/releases/download/latest/version.json";
    private const string ReleasesApiUrl =
        "https://api.github.com/repos/PatTheLad/PSMS/releases/tags/latest";
    private const string StableSetupAsset = "PSMS-Setup-win-x64.exe";

    private static readonly Regex VersionInBody =
        new(@"\*\*Version:\*\*\s*`?(?<v>\d+\.\d+\.\d+)`?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly PhotinoHost _host;
    private readonly object _gate = new();
    private CancellationTokenSource? _installCts;

    public AppUpdateService(PhotinoHost host)
    {
        _host = host;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PSMS", CurrentVersion));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string CurrentVersion { get; } = ResolveCurrentVersion();

    public AppUpdateInfo? Available { get; private set; }

    public bool IsChecking { get; private set; }

    public bool IsInstalling { get; private set; }

    public double InstallProgress { get; private set; }

    public string? LastError { get; private set; }

    public event Action? Changed;

    public async Task CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        IsChecking = true;
        LastError = null;
        Notify();

        try
        {
            var info = await TryReadVersionJsonAsync(cancellationToken)
                       ?? await TryReadGitHubApiAsync(cancellationToken);

            if (info is null)
            {
                Available = null;
                return;
            }

            Available = IsNewer(info.Version, CurrentVersion) ? info : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            Available = null;
        }
        finally
        {
            IsChecking = false;
            Notify();
        }
    }

    public async Task DownloadAndInstallAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            LastError = "In-app update is only available on Windows.";
            Notify();
            return;
        }

        var info = Available;
        if (info is null)
        {
            LastError = "No update available.";
            Notify();
            return;
        }

        lock (_gate)
        {
            if (IsInstalling)
            {
                return;
            }

            IsInstalling = true;
            InstallProgress = 0;
            LastError = null;
            _installCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        Notify();

        var ct = _installCts!.Token;
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "psms-update");
            Directory.CreateDirectory(tempDir);
            var setupPath = Path.Combine(tempDir, StableSetupAsset);

            using (var response = await _http.GetAsync(info.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? -1L;
                await using var remote = await response.Content.ReadAsStreamAsync(ct);
                await using var local = File.Create(setupPath);

                var buffer = new byte[81920];
                long readTotal = 0;
                int read;
                while ((read = await remote.ReadAsync(buffer, ct)) > 0)
                {
                    await local.WriteAsync(buffer.AsMemory(0, read), ct);
                    readTotal += read;
                    if (total > 0)
                    {
                        InstallProgress = Math.Clamp(100.0 * readTotal / total, 0, 99);
                        Notify();
                    }
                }
            }

            InstallProgress = 100;
            Notify();

            var start = new ProcessStartInfo
            {
                FileName = setupPath,
                UseShellExecute = true
            };
            Process.Start(start);

            // Give the installer a moment to start, then exit so files under Program Files can be replaced.
            await Task.Delay(800, CancellationToken.None);
            try
            {
                _host.Window?.Close();
            }
            catch
            {
                // ignored
            }

            Environment.Exit(0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
        }
        finally
        {
            IsInstalling = false;
            Notify();
        }
    }

    public void Dispose()
    {
        try
        {
            _installCts?.Cancel();
            _installCts?.Dispose();
        }
        catch
        {
            // ignored
        }

        _http.Dispose();
    }

    private async Task<AppUpdateInfo?> TryReadVersionJsonAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(VersionJsonUrl, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<VersionJsonDto>(stream, cancellationToken: ct);
        if (dto is null || string.IsNullOrWhiteSpace(dto.Version) || string.IsNullOrWhiteSpace(dto.SetupUrl))
        {
            return null;
        }

        return new AppUpdateInfo
        {
            Version = dto.Version.Trim(),
            SetupUrl = dto.SetupUrl.Trim(),
            ReleaseNotesUrl = $"https://github.com/{Owner}/{Repo}/releases/tag/latest"
        };
    }

    private async Task<AppUpdateInfo?> TryReadGitHubApiAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(ReleasesApiUrl, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var release = await JsonSerializer.DeserializeAsync<GitHubReleaseDto>(stream, cancellationToken: ct);
        if (release is null)
        {
            return null;
        }

        var version = ParseVersion(release.Body) ?? ParseVersion(release.Name);
        var asset = release.Assets?.FirstOrDefault(a =>
            string.Equals(a.Name, StableSetupAsset, StringComparison.OrdinalIgnoreCase));

        if (version is null || asset?.BrowserDownloadUrl is null)
        {
            return null;
        }

        return new AppUpdateInfo
        {
            Version = version,
            SetupUrl = asset.BrowserDownloadUrl,
            ReleaseNotesUrl = release.HtmlUrl
        };
    }

    private static string? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var m = VersionInBody.Match(text);
        if (m.Success)
        {
            return m.Groups["v"].Value;
        }

        // Fallback: first x.y.z in the text
        var loose = Regex.Match(text, @"\b(\d+\.\d+\.\d+)\b");
        return loose.Success ? loose.Groups[1].Value : null;
    }

    private static bool IsNewer(string remote, string local)
    {
        if (!Version.TryParse(Normalize(remote), out var r))
        {
            return false;
        }

        if (!Version.TryParse(Normalize(local), out var l))
        {
            return true;
        }

        return r > l;
    }

    private static string Normalize(string version)
    {
        // "1.0.42+sha" → "1.0.42"
        var plus = version.IndexOf('+');
        if (plus >= 0)
        {
            version = version[..plus];
        }

        var dash = version.IndexOf('-');
        if (dash >= 0)
        {
            version = version[..dash];
        }

        return version.Trim().TrimStart('v', 'V');
    }

    private static string ResolveCurrentVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            return Normalize(info);
        }

        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private void Notify() => Changed?.Invoke();

    private sealed class VersionJsonDto
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("setupUrl")]
        public string? SetupUrl { get; set; }
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAssetDto>? Assets { get; set; }
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
