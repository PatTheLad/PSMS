using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PSMS.App.Services;

public sealed class AppUpdateInfo
{
    public required string Version { get; init; }
    public string? MsiUrl { get; init; }
    public string? SetupUrl { get; init; }
    public string? ReleaseNotesUrl { get; init; }

    public string PackageUrl =>
        !string.IsNullOrWhiteSpace(MsiUrl) ? MsiUrl! :
        !string.IsNullOrWhiteSpace(SetupUrl) ? SetupUrl! :
        throw new InvalidOperationException("No download URL.");

    public bool IsSilentMsi => !string.IsNullOrWhiteSpace(MsiUrl);
}

/// <summary>
/// Checks the GitHub <c>latest</c> release and applies a quiet in-place upgrade (MSI).
/// The interactive Setup EXE is only for first-time installs.
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
    private const string StableMsiAsset = "PSMS-Setup-win-x64.msi";

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

            var useMsi = info.IsSilentMsi;
            var packageName = useMsi ? StableMsiAsset : StableSetupAsset;
            var packagePath = Path.Combine(tempDir, packageName);
            var downloadUrl = info.PackageUrl;

            await DownloadFileAsync(downloadUrl, packagePath, ct);

            InstallProgress = 100;
            Notify();

            var appExe = Environment.ProcessPath
                         ?? Path.Combine(AppContext.BaseDirectory, "PSMS.App.exe");

            // Apply quietly via helper so UAC / msiexec can finish after we exit.
            var helperPath = Path.Combine(tempDir, "apply-update.cmd");
            File.WriteAllText(helperPath, BuildApplyScript(packagePath, appExe, useMsi), Encoding.ASCII);

            var start = new ProcessStartInfo
            {
                FileName = helperPath,
                UseShellExecute = true,
                Verb = "runas", // elevate once; MSI upgrades Program Files
                WorkingDirectory = tempDir
            };

            try
            {
                Process.Start(start);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                LastError = "Update cancelled — administrator approval is required.";
                return;
            }

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

    private async Task DownloadFileAsync(string url, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var remote = await response.Content.ReadAsStreamAsync(ct);
        await using var local = File.Create(path);

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

    private static string BuildApplyScript(string packagePath, string appExe, bool useMsi)
    {
        // Wait for this process to exit, apply quietly, then relaunch.
        var installLine = useMsi
            ? $"msiexec /i \"{packagePath}\" /qn /norestart"
            : $"\"{packagePath}\" /quiet /norestart";

        return $"""
            @echo off
            setlocal
            rem Quiet in-app upgrade — no Setup UI
            timeout /t 2 /nobreak >nul
            {installLine}
            set ERR=%ERRORLEVEL%
            if %ERR%==0 goto relaunch
            if %ERR%==3010 goto relaunch
            exit /b %ERR%
            :relaunch
            start "" "{appExe}"
            """;
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
        if (dto is null || string.IsNullOrWhiteSpace(dto.Version))
        {
            return null;
        }

        var msi = dto.MsiUrl?.Trim();
        var setup = dto.SetupUrl?.Trim();
        if (string.IsNullOrWhiteSpace(msi) && string.IsNullOrWhiteSpace(setup))
        {
            return null;
        }

        return new AppUpdateInfo
        {
            Version = dto.Version.Trim(),
            MsiUrl = msi,
            SetupUrl = setup,
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
        var msi = release.Assets?.FirstOrDefault(a =>
            string.Equals(a.Name, StableMsiAsset, StringComparison.OrdinalIgnoreCase));
        var setup = release.Assets?.FirstOrDefault(a =>
            string.Equals(a.Name, StableSetupAsset, StringComparison.OrdinalIgnoreCase));

        if (version is null || (msi?.BrowserDownloadUrl is null && setup?.BrowserDownloadUrl is null))
        {
            return null;
        }

        return new AppUpdateInfo
        {
            Version = version,
            MsiUrl = msi?.BrowserDownloadUrl,
            SetupUrl = setup?.BrowserDownloadUrl,
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

        [JsonPropertyName("msiUrl")]
        public string? MsiUrl { get; set; }

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
