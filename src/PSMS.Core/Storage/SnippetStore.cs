using System.Text.Json;
using PSMS.Core.Models;

namespace PSMS.Core.Storage;

public sealed class SnippetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<SavedSnippet>? _cache;

    public event Action? Changed;

    public SnippetStore()
    {
        Directory.CreateDirectory(PsmsPaths.GetAppDataRoot());
        _path = Path.Combine(PsmsPaths.GetAppDataRoot(), "snippets.json");
    }

    public async Task<IReadOnlyList<SavedSnippet>> GetAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return (await LoadUnlockedAsync().ConfigureAwait(false))
                .OrderByDescending(s => s.UpdatedAt)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAsync(SavedSnippet snippet)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var list = await LoadUnlockedAsync().ConfigureAwait(false);
            var existing = list.FirstOrDefault(s => s.Id == snippet.Id);
            if (existing is not null)
            {
                existing.Title = snippet.Title;
                existing.Sql = snippet.Sql;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                snippet.UpdatedAt = DateTimeOffset.UtcNow;
                list.Add(snippet);
            }

            await SaveUnlockedAsync(list).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    public async Task DeleteAsync(Guid id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var list = await LoadUnlockedAsync().ConfigureAwait(false);
            list.RemoveAll(s => s.Id == id);
            await SaveUnlockedAsync(list).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    private async Task<List<SavedSnippet>> LoadUnlockedAsync()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        if (!File.Exists(_path))
        {
            _cache = [];
            return _cache;
        }

        await using var stream = File.OpenRead(_path);
        _cache = await JsonSerializer.DeserializeAsync<List<SavedSnippet>>(stream, JsonOptions).ConfigureAwait(false)
                 ?? [];
        return _cache;
    }

    private async Task SaveUnlockedAsync(List<SavedSnippet> list)
    {
        _cache = list;
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, list, JsonOptions).ConfigureAwait(false);
    }
}
