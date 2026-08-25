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

public sealed class ActiveSessionService
{
    private readonly List<ConnectionDefinition> _openConnections = [];
    private readonly List<QueryTab> _tabs = [];

    public event Action? Changed;
    public event Action? ConnectionsChanged;

    public IReadOnlyList<ConnectionDefinition> OpenConnections => _openConnections;
    public IReadOnlyList<QueryTab> Tabs => _tabs;
    public Guid? ActiveTabId { get; private set; }
    public Guid? SelectedConnectionId { get; private set; }
    public QueryTab? ActiveTab => _tabs.FirstOrDefault(t => t.Id == ActiveTabId);
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
        Notify();
        return tab;
    }

    public void ActivateTab(Guid tabId)
    {
        if (_tabs.All(t => t.Id != tabId) || ActiveTabId == tabId)
        {
            return;
        }

        ActiveTabId = tabId;
        Notify();
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

    public void Notify() => Changed?.Invoke();

    public void NotifyConnectionsChanged() => ConnectionsChanged?.Invoke();
}
