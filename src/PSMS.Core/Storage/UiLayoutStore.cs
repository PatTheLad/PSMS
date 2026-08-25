using System.Text.Json;

namespace PSMS.Core.Storage;

public sealed class UiLayoutSettings
{
    public double ExplorerWidth { get; set; } = 280;
    public double EditorRatio { get; set; } = 0.55;
}

public sealed class UiLayoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UiLayoutSettings? _cache;

    public UiLayoutStore()
    {
        var root = PsmsPaths.GetAppDataRoot();
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "ui-layout.json");
    }

    public async Task<UiLayoutSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache is not null)
            {
                return Clone(_cache);
            }

            if (!File.Exists(_path))
            {
                _cache = new UiLayoutSettings();
                return Clone(_cache);
            }

            await using var stream = File.OpenRead(_path);
            _cache = await JsonSerializer.DeserializeAsync<UiLayoutSettings>(stream, JsonOptions, cancellationToken)
                         .ConfigureAwait(false)
                     ?? new UiLayoutSettings();
            Normalize(_cache);
            return Clone(_cache);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(double explorerWidth, double editorRatio, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cache = new UiLayoutSettings
            {
                ExplorerWidth = explorerWidth,
                EditorRatio = editorRatio
            };
            Normalize(_cache);
            await using var stream = File.Create(_path);
            await JsonSerializer.SerializeAsync(stream, _cache, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Normalize(UiLayoutSettings s)
    {
        s.ExplorerWidth = Math.Clamp(s.ExplorerWidth, 220, 480);
        s.EditorRatio = Math.Clamp(s.EditorRatio, 0.2, 0.8);
    }

    private static UiLayoutSettings Clone(UiLayoutSettings s) => new()
    {
        ExplorerWidth = s.ExplorerWidth,
        EditorRatio = s.EditorRatio
    };
}
