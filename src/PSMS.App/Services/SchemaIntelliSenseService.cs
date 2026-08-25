using System.Collections.Concurrent;
using PSMS.Core.Abstractions;
using PSMS.Core.Models;

namespace PSMS.App.Services;

public sealed class IntelliSenseSnapshot
{
    public required Guid ConnectionId { get; init; }
    public required string Database { get; init; }
    public required IReadOnlyList<string> Databases { get; init; }
    public required IReadOnlyList<CatalogObjectInfo> Objects { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<ColumnInfo>> ColumnsByTable { get; init; }
    public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.UtcNow;
    public int TableCount => Objects.Count(o => o.Kind is CatalogObjectKind.Table or CatalogObjectKind.View);
    public int ObjectCount => Databases.Count + Objects.Count + ColumnsByTable.Values.Sum(c => c.Count);
    public string Status { get; init; } = "Ready";
}

public sealed class SchemaIntelliSenseService
{
    private readonly IDbProviderFactory _factory;
    private readonly IConnectionStore _store;
    private readonly ConcurrentDictionary<string, IntelliSenseSnapshot> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public SchemaIntelliSenseService(IDbProviderFactory factory, IConnectionStore store)
    {
        _factory = factory;
        _store = store;
    }

    public IntelliSenseSnapshot? Current { get; private set; }

    public async Task<IntelliSenseSnapshot?> EnsureAsync(
        ConnectionDefinition connection,
        string database,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var key = CacheKey(connection.Id, database);
        if (!forceRefresh && _cache.TryGetValue(key, out var cached))
        {
            Current = cached;
            Changed?.Invoke();
            return cached;
        }

        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _cache.TryGetValue(key, out cached))
            {
                Current = cached;
                Changed?.Invoke();
                return cached;
            }

            // Publish a loading placeholder so the UI can show progress.
            Current = new IntelliSenseSnapshot
            {
                ConnectionId = connection.Id,
                Database = database,
                Databases = [],
                Objects = [],
                ColumnsByTable = new Dictionary<string, IReadOnlyList<ColumnInfo>>(StringComparer.OrdinalIgnoreCase),
                Status = "Loading…"
            };
            Changed?.Invoke();

            var password = connection.UseWindowsAuth ? null : _store.DecryptPassword(connection);
            var provider = _factory.GetProvider(connection.Engine);

            var databases = (await provider.GetDatabasesAsync(connection, password, cancellationToken)
                    .ConfigureAwait(false))
                .Select(d => d.Name)
                .ToList();

            // Full catalog (objects + columns) for the active database.
            var activeObjectsTask = provider.GetCatalogObjectsAsync(connection, password, database, cancellationToken);
            var columnsTask = provider.GetAllColumnsAsync(connection, password, database, cancellationToken);
            await Task.WhenAll(activeObjectsTask, columnsTask).ConfigureAwait(false);

            var activeObjects = (await activeObjectsTask.ConfigureAwait(false))
                .Select(o => o with { Database = database })
                .ToList();
            var columns = await columnsTask.ConfigureAwait(false);

            var byTable = columns
                .GroupBy(c => $"{c.Schema}.{c.Table}", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<ColumnInfo>)g.ToList(),
                    StringComparer.OrdinalIgnoreCase);

            // Table/view names from every other database (for cross-db completion).
            var otherObjects = await LoadTablesFromOtherDatabasesAsync(
                    provider, connection, password, databases, database, cancellationToken)
                .ConfigureAwait(false);

            var allObjects = activeObjects
                .Concat(otherObjects)
                .GroupBy(o => $"{o.Database}|{o.Schema}|{o.Name}|{o.Kind}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            var snapshot = new IntelliSenseSnapshot
            {
                ConnectionId = connection.Id,
                Database = database,
                Databases = databases,
                Objects = allObjects,
                ColumnsByTable = byTable,
                Status = "Ready"
            };

            _cache[key] = snapshot;
            Current = snapshot;
            Changed?.Invoke();
            return snapshot;
        }
        catch (Exception ex)
        {
            var failed = new IntelliSenseSnapshot
            {
                ConnectionId = connection.Id,
                Database = database,
                Databases = [],
                Objects = [],
                ColumnsByTable = new Dictionary<string, IReadOnlyList<ColumnInfo>>(StringComparer.OrdinalIgnoreCase),
                Status = $"Error: {ex.Message}"
            };
            Current = failed;
            Changed?.Invoke();
            return failed;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<List<CatalogObjectInfo>> LoadTablesFromOtherDatabasesAsync(
        IDbProvider provider,
        ConnectionDefinition connection,
        string? password,
        IReadOnlyList<string> databases,
        string activeDatabase,
        CancellationToken cancellationToken)
    {
        var bag = new ConcurrentBag<CatalogObjectInfo>();
        var others = databases
            .Where(d => !string.Equals(d, activeDatabase, StringComparison.OrdinalIgnoreCase))
            .ToList();

        await Parallel.ForEachAsync(
            others,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = cancellationToken
            },
            async (db, ct) =>
            {
                try
                {
                    var objs = await provider.GetCatalogObjectsAsync(connection, password, db, ct)
                        .ConfigureAwait(false);
                    foreach (var o in objs)
                    {
                        if (o.Kind is not (CatalogObjectKind.Table or CatalogObjectKind.View))
                        {
                            continue;
                        }

                        bag.Add(o with { Database = db });
                    }
                }
                catch
                {
                    // Skip databases the login cannot access.
                }
            }).ConfigureAwait(false);

        return bag.ToList();
    }

    public void Invalidate(Guid? connectionId = null, string? database = null)
    {
        if (connectionId is null)
        {
            _cache.Clear();
            Current = null;
            Changed?.Invoke();
            return;
        }

        var prefix = connectionId.Value.ToString();
        foreach (var key in _cache.Keys.Where(k =>
                     k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                     && (database is null || k.EndsWith("|" + database, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            _cache.TryRemove(key, out _);
        }

        if (Current is not null
            && Current.ConnectionId == connectionId
            && (database is null || string.Equals(Current.Database, database, StringComparison.OrdinalIgnoreCase)))
        {
            Current = null;
        }

        Changed?.Invoke();
    }

    private static string CacheKey(Guid connectionId, string database) => $"{connectionId}|{database}";
}
