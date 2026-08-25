namespace PSMS.App.Services;

public sealed class EditorRegistry
{
    private readonly Dictionary<Guid, Func<Task<string>>> _allGetters = new();
    private readonly Dictionary<Guid, Func<Task<string>>> _selectedOrAllGetters = new();
    private readonly Dictionary<Guid, Func<string, Task>> _setters = new();

    public void Register(
        Guid tabId,
        Func<Task<string>> getAll,
        Func<Task<string>> getSelectedOrAll,
        Func<string, Task> setValue)
    {
        _allGetters[tabId] = getAll;
        _selectedOrAllGetters[tabId] = getSelectedOrAll;
        _setters[tabId] = setValue;
    }

    public void Unregister(Guid tabId)
    {
        _allGetters.Remove(tabId);
        _selectedOrAllGetters.Remove(tabId);
        _setters.Remove(tabId);
    }

    public async Task<string?> GetSqlAsync(Guid tabId)
    {
        if (_allGetters.TryGetValue(tabId, out var getter))
        {
            return await getter();
        }

        return null;
    }

    public async Task<string?> GetSelectedOrAllSqlAsync(Guid tabId)
    {
        if (_selectedOrAllGetters.TryGetValue(tabId, out var getter))
        {
            return await getter();
        }

        return await GetSqlAsync(tabId);
    }

    public async Task SetSqlAsync(Guid tabId, string sql)
    {
        if (_setters.TryGetValue(tabId, out var setter))
        {
            await setter(sql);
        }
    }
}
