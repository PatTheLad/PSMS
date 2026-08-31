using System.Text.Json;

namespace PSMS.Core.Storage;

public sealed class SessionTabSnapshot
{
    public Guid Id { get; set; }
    public Guid ConnectionId { get; set; }
    public string Database { get; set; } = "master";
    public string Sql { get; set; } = "SELECT 1 AS Value;";
    public string Title { get; set; } = "Query";
    public string? FilePath { get; set; }
    public bool IsDirty { get; set; }
}

public sealed class SessionSnapshot
{
    public List<SessionTabSnapshot> Tabs { get; set; } = [];
    public Guid? ActiveTabId { get; set; }
}

public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _debounce;

    public SessionStore()
    {
        var root = PsmsPaths.GetAppDataRoot();
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "session.json");
    }

    public async Task<SessionSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return new SessionSnapshot();
            }

            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<SessionSnapshot>(stream, JsonOptions, cancellationToken)
                       .ConfigureAwait(false)
                   ?? new SessionSnapshot();
        }
        catch
        {
            return new SessionSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = File.Create(_path);
            await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Debounced save so rapid tab edits do not hammer disk.</summary>
    public void ScheduleSave(Func<SessionSnapshot> snapshotFactory)
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = new CancellationTokenSource();
        var token = _debounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(600, token).ConfigureAwait(false);
                await SaveAsync(snapshotFactory(), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // newer save scheduled
            }
            catch
            {
                // ignore disk errors
            }
        }, CancellationToken.None);
    }
}
