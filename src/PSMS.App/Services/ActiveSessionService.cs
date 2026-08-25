using PSMS.Core.Models;

namespace PSMS.App.Services;

public sealed class QueryTab
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; set; } = "Query";
    public Guid ConnectionId { get; set; }
    public string Database { get; set; } = "master";
    public string Sql { get; set; } = "SELECT 1 AS Value;";
    public string? FilePath { get; set; }
    public bool IsDirty { get; set; }
    public QueryResult? LastResult { get; set; }
    public bool IsExecuting { get; set; }
}

public sealed class PinnedResult
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; set; } = "Pinned";
    public QueryResult Result { get; set; } = new();
    public DateTimeOffset PinnedAt { get; set; } = DateTimeOffset.Now;
    public Guid? SourceTabId { get; set; }
}

public sealed class ActiveSessionService
{
    private readonly List<ConnectionDefinition> _openConnections = [];
    private readonly List<QueryTab> _tabs = [];
    private readonly List<PinnedResult> _pinned = [];

    public event Action? Changed;
    public event Action? ConnectionsChanged;

    public IReadOnlyList<ConnectionDefinition> OpenConnections => _openConnections;
    public IReadOnlyList<QueryTab> Tabs => _tabs;
    public IReadOnlyList<PinnedResult> PinnedResults => _pinned;
    public Guid? ActiveTabId { get; private set; }
    public Guid? SelectedConnectionId { get; private set; }
    /// <summary>When set, ResultsPane shows this pinned snapshot instead of the live tab result.</summary>
    public Guid? ViewingPinnedId { get; private set; }

    public QueryTab? ActiveTab => _tabs.FirstOrDefault(t => t.Id == ActiveTabId);
    public PinnedResult? ViewingPinned =>
        ViewingPinnedId is Guid id ? _pinned.FirstOrDefault(p => p.Id == id) : null;

    public ConnectionDefinition? SelectedConnection =>
        SelectedConnectionId is Guid id
            ? _openConnections.FirstOrDefault(c => c.Id == id)
              ?? null
            : _openConnections.FirstOrDefault();

    public double ExplorerWidth { get; set; } = 280;
    public double EditorRatio { get; set; } = 0.55;

    /// <summary>When true, the main pane shows SQL Server Admin instead of the query workspace.</summary>
    public bool ShowAdminWorkspace { get; private set; }

    public void ShowAdmin()
    {
        ShowAdminWorkspace = true;
        Notify();
    }

    public void ShowQueries()
    {
        ShowAdminWorkspace = false;
        Notify();
    }

    public void SelectConnection(Guid connectionId)
    {
        SelectedConnectionId = connectionId;
        Notify();
    }

    public void MarkConnected(ConnectionDefinition connection)
    {
        var existing = _openConnections.FirstOrDefault(c => c.Id == connection.Id);
        if (existing is null)
        {
            _openConnections.Add(connection);
        }
        else
        {
            var index = _openConnections.IndexOf(existing);
            _openConnections[index] = connection;
        }

        SelectedConnectionId = connection.Id;
        Notify();
    }

    public void MarkDisconnected(Guid connectionId)
    {
        _openConnections.RemoveAll(c => c.Id == connectionId);
        if (SelectedConnectionId == connectionId)
        {
            SelectedConnectionId = _openConnections.FirstOrDefault()?.Id;
        }

        Notify();
    }

    public bool IsConnected(Guid connectionId) => _openConnections.Any(c => c.Id == connectionId);

    public QueryTab OpenQueryTab(ConnectionDefinition connection, string database, string? sql = null, string? title = null)
    {
        MarkConnected(connection);
        var tab = new QueryTab
        {
            Title = title ?? $"{connection.Name} — {database}",
            ConnectionId = connection.Id,
            Database = string.IsNullOrWhiteSpace(database) ? (connection.Database ?? "master") : database,
            Sql = sql ?? "SELECT 1 AS Value;"
        };
        _tabs.Add(tab);
        ActiveTabId = tab.Id;
        ViewingPinnedId = null;
        Notify();
        return tab;
    }

    public QueryTab? DuplicateActiveTab()
    {
        var src = ActiveTab;
        if (src is null)
        {
            return null;
        }

        var conn = _openConnections.FirstOrDefault(c => c.Id == src.ConnectionId);
        if (conn is null)
        {
            return null;
        }

        return OpenQueryTab(conn, src.Database, src.Sql, $"{src.Title} (copy)");
    }

    public void ActivateTab(Guid tabId)
    {
        if (_tabs.All(t => t.Id != tabId) || ActiveTabId == tabId)
        {
            return;
        }

        ActiveTabId = tabId;
        ViewingPinnedId = null;
        Notify();
    }

    public bool CycleTab(int delta)
    {
        if (_tabs.Count == 0)
        {
            return false;
        }

        var index = ActiveTabId is Guid id
            ? _tabs.FindIndex(t => t.Id == id)
            : 0;
        if (index < 0)
        {
            index = 0;
        }

        var next = (index + delta) % _tabs.Count;
        if (next < 0)
        {
            next += _tabs.Count;
        }

        if (_tabs[next].Id == ActiveTabId)
        {
            return false;
        }

        ActiveTabId = _tabs[next].Id;
        ViewingPinnedId = null;
        Notify();
        return true;
    }

    public void CloseTab(Guid tabId)
    {
        var tab = _tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null)
        {
            return;
        }

        _tabs.Remove(tab);
        if (ActiveTabId == tabId)
        {
            ActiveTabId = _tabs.LastOrDefault()?.Id;
        }

        Notify();
    }

    public PinnedResult? PinCurrentResult(string? title = null)
    {
        var result = ActiveTab?.LastResult;
        if (result is null || !result.HasResultSet)
        {
            return null;
        }

        var pin = new PinnedResult
        {
            Title = title ?? $"Pin {DateTime.Now:HH:mm:ss} · {result.ResultSets.Sum(r => r.Rows.Count):N0} rows",
            Result = result,
            SourceTabId = ActiveTabId
        };
        _pinned.Add(pin);
        ViewingPinnedId = pin.Id;
        Notify();
        return pin;
    }

    public void Unpin(Guid pinId)
    {
        _pinned.RemoveAll(p => p.Id == pinId);
        if (ViewingPinnedId == pinId)
        {
            ViewingPinnedId = null;
        }

        Notify();
    }

    public void ViewPinned(Guid? pinId)
    {
        ViewingPinnedId = pinId;
        Notify();
    }

    public void ViewLiveResults()
    {
        ViewingPinnedId = null;
        Notify();
    }

    public void Notify() => Changed?.Invoke();

    public void NotifyConnectionsChanged() => ConnectionsChanged?.Invoke();
}
