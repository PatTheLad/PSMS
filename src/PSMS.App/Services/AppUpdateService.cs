using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
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
    public string? MacOsArm64Url { get; init; }
    public string? MacOsX64Url { get; init; }
    public string? ReleaseNotesUrl { get; init; }

    public string? ResolvePackageUrl()
    {
        if (OperatingSystem.IsWindows())
        {
            if (!string.IsNullOrWhiteSpace(MsiUrl))
            {
                return MsiUrl;
            }

            return string.IsNullOrWhiteSpace(SetupUrl) ? null : SetupUrl;
        }

        if (OperatingSystem.IsMacOS())
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                && !string.IsNullOrWhiteSpace(MacOsArm64Url))
            {
                return MacOsArm64Url;
            }

            if (RuntimeInformation.ProcessArchitecture == Architecture.X64
                && !string.IsNullOrWhiteSpace(MacOsX64Url))
            {
                return MacOsX64Url;
            }

            // Prefer arm64 URL as fallback when only one arch is published.
            return !string.IsNullOrWhiteSpace(MacOsArm64Url) ? MacOsArm64Url : MacOsX64Url;
        }

        return null;
    }

    public bool IsSilentMsi => OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(MsiUrl);
}

/// <summary>
/// Checks the GitHub <c>latest</c> release and applies a quiet in-place upgrade.
/// Windows: MSI via msiexec /qn. macOS: replace PSMS.app from zip. Setup EXE/DMG are first-time installers.
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
    private const string StableMacArm64Zip = "PSMS-osx-arm64.app.zip";
    private const string StableMacX64Zip = "PSMS-osx-x64.app.zip";

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

    public bool SupportsInAppUpdate => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public async Task CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (!SupportsInAppUpdate)
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

            if (info is null || info.ResolvePackageUrl() is null)
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
        if (!SupportsInAppUpdate)
        {
            LastError = "In-app update is only available on Windows and macOS.";
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

        var packageUrl = info.ResolvePackageUrl();
        if (string.IsNullOrWhiteSpace(packageUrl))
        {
            LastError = "No package URL for this platform.";
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
            if (OperatingSystem.IsMacOS())
            {
                await ApplyMacOsUpdateAsync(packageUrl, ct);
            }
            else
            {
                await ApplyWindowsUpdateAsync(info, packageUrl, ct);
            }
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

    private async Task ApplyWindowsUpdateAsync(AppUpdateInfo info, string packageUrl, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "psms-update");
        Directory.CreateDirectory(tempDir);

        var useMsi = info.IsSilentMsi;
        var packageName = useMsi ? StableMsiAsset : StableSetupAsset;
        var packagePath = Path.Combine(tempDir, packageName);

        await DownloadFileAsync(packageUrl, packagePath, ct);
        InstallProgress = 100;
        Notify();

        var appExe = Environment.ProcessPath
                     ?? Path.Combine(AppContext.BaseDirectory, "PSMS.App.exe");

        var helperPath = Path.Combine(tempDir, "apply-update.cmd");
        File.WriteAllText(helperPath, BuildWindowsApplyScript(packagePath, appExe, useMsi), Encoding.ASCII);

        var start = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = true,
            Verb = "runas",
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

        ExitApp();
    }

    private async Task ApplyMacOsUpdateAsync(string packageUrl, CancellationToken ct)
    {
        var appBundle = FindMacAppBundle();
        if (appBundle is null)
        {
            LastError = "Could not locate PSMS.app. Install from the DMG into Applications (or ~/Applications), then try again.";
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "psms-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        var zipPath = Path.Combine(tempDir, "PSMS.app.zip");
        await DownloadFileAsync(packageUrl, zipPath, ct);

        var extractDir = Path.Combine(tempDir, "extract");
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        var newApp = Directory.EnumerateDirectories(extractDir, "*.app", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (newApp is null)
        {
            LastError = "Downloaded update did not contain a .app bundle.";
            return;
        }

        InstallProgress = 100;
        Notify();

        var helperPath = Path.Combine(tempDir, "apply-update.sh");
        File.WriteAllText(helperPath, BuildMacApplyScript(newApp, appBundle), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var chmod = new ProcessStartInfo
        {
            FileName = "/bin/chmod",
            ArgumentList = { "+x", helperPath },
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process.Start(chmod)?.WaitForExit(5000);

        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/bash",
            ArgumentList = { helperPath },
            UseShellExecute = false,
            CreateNoWindow = true
        });

        ExitApp();
    }

    private void ExitApp()
    {
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

    private static string BuildWindowsApplyScript(string packagePath, string appExe, bool useMsi)
    {
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

    private static string BuildMacApplyScript(string newAppPath, string targetAppPath)
    {
        // Escape for single-quoted bash strings
        static string Q(string p) => p.Replace("'", "'\\''", StringComparison.Ordinal);

        return $$"""
            #!/bin/bash
            set -euo pipefail
            sleep 2
            TARGET='{{Q(targetAppPath)}}'
            NEW='{{Q(newAppPath)}}'
            BACKUP="${TARGET}.bak-$$"
            # Replace bundle in place (works for ~/Applications without admin)
            if [[ -d "$TARGET" ]]; then
              mv "$TARGET" "$BACKUP" || true
            fi
            ditto "$NEW" "$TARGET"
            xattr -cr "$TARGET" 2>/dev/null || true
            rm -rf "$BACKUP" 2>/dev/null || true
            open "$TARGET"
            """;
    }

    private static string? FindMacAppBundle()
    {
        var candidates = new List<string?>
        {
            Environment.ProcessPath,
            AppContext.BaseDirectory
        };

        foreach (var start in candidates)
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            var dir = Directory.Exists(start) ? start : Path.GetDirectoryName(start);
            while (!string.IsNullOrEmpty(dir))
            {
                if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(dir, "Contents", "Info.plist")))
                {
                    return dir;
                }

                dir = Path.GetDirectoryName(dir);
            }
        }

        // Common install locations when launched oddly
        foreach (var guess in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "PSMS.app"),
                     "/Applications/PSMS.app"
                 })
        {
            if (Directory.Exists(guess))
            {
                return guess;
            }
        }

        return null;
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

        return new AppUpdateInfo
        {
            Version = dto.Version.Trim(),
            MsiUrl = NullIfEmpty(dto.MsiUrl),
            SetupUrl = NullIfEmpty(dto.SetupUrl),
            MacOsArm64Url = NullIfEmpty(dto.MacOsArm64Url),
            MacOsX64Url = NullIfEmpty(dto.MacOsX64Url),
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
        if (version is null)
        {
            return null;
        }

        string? Find(string name) =>
            release.Assets?.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                ?.BrowserDownloadUrl;

        return new AppUpdateInfo
        {
            Version = version,
            MsiUrl = Find(StableMsiAsset),
            SetupUrl = Find(StableSetupAsset),
            MacOsArm64Url = Find(StableMacArm64Zip),
            MacOsX64Url = Find(StableMacX64Zip),
            ReleaseNotesUrl = release.HtmlUrl
        };
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

        [JsonPropertyName("macOsArm64Url")]
        public string? MacOsArm64Url { get; set; }

        [JsonPropertyName("macOsX64Url")]
        public string? MacOsX64Url { get; set; }
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
