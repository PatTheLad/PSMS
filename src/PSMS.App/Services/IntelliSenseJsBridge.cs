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

            // Full catalogs — no caps. Serialize to JSON once for reliable large payloads.
            var payload = new
            {
                currentDatabase = snap.Database,
                databases = snap.Databases.ToList(),
                objects = snap.Objects.Select(o => new
                {
                    schema = o.Schema,
                    name = o.Name,
                    kind = o.Kind.ToString(),
                    database = o.Database ?? snap.Database
                }).ToList(),
                columns = snap.ColumnsByTable
                    .SelectMany(kv => kv.Value)
                    .Select(c => new
                    {
                        schema = c.Schema,
                        table = c.Table,
                        name = c.Name,
                        dataType = c.DataType
                    }).ToList()
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
