using System.Text.Json;
using Microsoft.JSInterop;
using PSMS.Core.Models;

namespace PSMS.App.Services;

/// <summary>
/// Pushes the loaded catalog into a pure-JS Monaco completion provider.
/// Must be bound from a component so JS runs on the WebView sync context.
/// </summary>
public sealed class IntelliSenseJsBridge : IDisposable
{
    /// <summary>Hard cap so Photino WebView doesn't OOM on huge servers.</summary>
    private const int MaxObjectsForJs = 4_000;
    private const int MaxColumnsForJs = 8_000;
    private const int MaxDatabasesForJs = 150;

    /// <summary>True after the last push if the in-memory catalog was truncated for JS.</summary>
    public bool LastPushTruncated { get; private set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SchemaIntelliSenseService _intelliSense;
    private readonly object _gate = new();
    private IJSRuntime? _js;
    private Func<Func<Task>, Task>? _invokeAsync;
    private int _pushVersion;
    private bool _subscribed;
    private bool _disposed;

    public IntelliSenseJsBridge(SchemaIntelliSenseService intelliSense)
    {
        _intelliSense = intelliSense;
    }

    /// <summary>Call once from a rooted component (e.g. App) with its IJSRuntime + InvokeAsync.</summary>
    public void Bind(IJSRuntime js, Func<Func<Task>, Task> invokeAsync)
    {
        lock (_gate)
        {
            _js = js;
            _invokeAsync = invokeAsync;
            if (!_subscribed)
            {
                _intelliSense.Changed += OnChanged;
                _subscribed = true;
            }
        }

        _ = QueuePushAsync();
    }

    public Task EnsureRegisteredAsync() => QueuePushAsync(registerOnly: true);

    public Task PushCurrentAsync() => QueuePushAsync();

    private void OnChanged() => _ = QueuePushAsync();

    private async Task QueuePushAsync(bool registerOnly = false)
    {
        Func<Func<Task>, Task>? invoke;
        IJSRuntime? js;
        lock (_gate)
        {
            invoke = _invokeAsync;
            js = _js;
        }

        if (invoke is null || js is null || _disposed)
        {
            return;
        }

        var version = Interlocked.Increment(ref _pushVersion);

        try
        {
            await invoke(() => PushOnUiAsync(js, version, registerOnly));
        }
        catch
        {
            // WebView may not be ready yet.
        }
    }

    private async Task PushOnUiAsync(IJSRuntime js, int version, bool registerOnly)
    {
        if (_disposed || version != Volatile.Read(ref _pushVersion))
        {
            return;
        }

        try
        {
            await js.InvokeVoidAsync("psmsIntelliSense.ensureRegistered");
            if (registerOnly)
            {
                return;
            }

            var snap = _intelliSense.Current;
            if (snap is null
                || snap.Status.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                || snap.Status.StartsWith("Loading", StringComparison.OrdinalIgnoreCase))
            {
                if (snap is null || snap.Status.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                {
                    await js.InvokeVoidAsync("psmsIntelliSense.setCatalogJson", "null");
                }

                return;
            }

            var currentDb = snap.Database;
            var objects = snap.Objects
                .OrderBy(o =>
                {
                    var same = string.Equals(o.Database ?? currentDb, currentDb, StringComparison.OrdinalIgnoreCase);
                    return same ? 0 : 1;
                })
                .ThenBy(o => o.Kind)
                .ThenBy(o => o.Schema, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxObjectsForJs)
                .Select(o => new
                {
                    s = o.Schema,
                    n = o.Name,
                    k = KindCode(o.Kind),
                    d = string.Equals(o.Database ?? currentDb, currentDb, StringComparison.OrdinalIgnoreCase)
                        ? null
                        : (o.Database ?? currentDb)
                })
                .ToList();

            var columns = snap.ColumnsByTable
                .SelectMany(kv => kv.Value)
                .Take(MaxColumnsForJs)
                .Select(c => new
                {
                    s = c.Schema,
                    t = c.Table,
                    n = c.Name,
                    ty = c.DataType
                })
                .ToList();

            LastPushTruncated = snap.Objects.Count > MaxObjectsForJs
                                || snap.ColumnsByTable.Sum(kv => kv.Value.Count) > MaxColumnsForJs
                                || snap.Databases.Count > MaxDatabasesForJs;

            var payload = new
            {
                currentDatabase = currentDb,
                databases = snap.Databases.Take(MaxDatabasesForJs).ToList(),
                objects,
                columns,
                truncated = LastPushTruncated
            };

            var json = JsonSerializer.Serialize(payload, JsonOptions);

            if (version != Volatile.Read(ref _pushVersion))
            {
                return;
            }

            await js.InvokeVoidAsync("psmsIntelliSense.setCatalogJson", json);
        }
        catch
        {
            // Monaco / script may not be ready; editor init will retry.
        }
    }

    private static string KindCode(CatalogObjectKind kind) => kind switch
    {
        CatalogObjectKind.Schema => "S",
        CatalogObjectKind.Table => "T",
        CatalogObjectKind.View => "V",
        CatalogObjectKind.Procedure => "P",
        CatalogObjectKind.Function => "F",
        CatalogObjectKind.Column => "C",
        _ => "T"
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_subscribed)
        {
            _intelliSense.Changed -= OnChanged;
        }

        lock (_gate)
        {
            _js = null;
            _invokeAsync = null;
        }
    }
}
