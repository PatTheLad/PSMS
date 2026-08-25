using System.Text.Json;
using PSMS.Core.Models;

namespace PSMS.Core.Storage;

public sealed class QueryHistoryStore
{
    private const int MaxEntries = 250;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<QueryHistoryEntry>? _cache;

    public QueryHistoryStore()
    {
        var root = PsmsPaths.GetAppDataRoot();
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "query-history.json");
    }

    public event Action? Changed;

    public async Task<IReadOnlyList<QueryHistoryEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAsync(QueryHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = (await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false)).ToList();
            list.Insert(0, entry);
            if (list.Count > MaxEntries)
            {
                list = list.Take(MaxEntries).ToList();
            }

            _cache = list;
            await WriteUnlockedAsync(list, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cache = [];
            await WriteUnlockedAsync([], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    private async Task<List<QueryHistoryEntry>> ReadUnlockedAsync(CancellationToken cancellationToken)
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
        var data = await JsonSerializer.DeserializeAsync<List<QueryHistoryEntry>>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        _cache = data ?? [];
        return _cache;
    }

    private async Task WriteUnlockedAsync(List<QueryHistoryEntry> entries, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}
