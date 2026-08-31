using Photino.NET;

namespace PSMS.App.Services;

public sealed class PhotinoHost
{
    public PhotinoWindow? Window { get; set; }
}

public interface IFileDialogService
{
    Task<string?> OpenSqlFileAsync();
    Task<string?> OpenDatabaseFileAsync(bool access);
    Task<string?> SaveSqlFileAsync(string? defaultFileName = null);
    Task<string?> SaveCsvFileAsync(string? defaultFileName = null);
    Task<string?> SaveJsonFileAsync(string? defaultFileName = null);
}

public sealed class PhotinoFileDialogService : IFileDialogService
{
    private readonly PhotinoHost _host;

    public PhotinoFileDialogService(PhotinoHost host) => _host = host;

    public async Task<string?> OpenSqlFileAsync()
    {
        var window = _host.Window;
        if (window is null)
        {
            return null;
        }

        var filters = new (string, string[])[]
        {
            ("SQL files", ["sql"]),
            ("All files", ["*"])
        };

        var paths = await window.ShowOpenFileAsync(
            "Open SQL script",
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
            multiSelect: false,
            filters);

        return paths is { Length: > 0 } ? paths[0] : null;
    }

    public async Task<string?> OpenDatabaseFileAsync(bool access)
    {
        var window = _host.Window;
        if (window is null)
        {
            return null;
        }

        var filters = access
            ? new (string, string[])[]
            {
                ("Access databases", ["accdb", "mdb"]),
                ("All files", ["*"])
            }
            : new (string, string[])[]
            {
                ("SQLite databases", ["db", "sqlite", "sqlite3"]),
                ("All files", ["*"])
            };

        var paths = await window.ShowOpenFileAsync(
            access ? "Open Access database" : "Open SQLite database",
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
            multiSelect: false,
            filters);

        return paths is { Length: > 0 } ? paths[0] : null;
    }

    public async Task<string?> SaveSqlFileAsync(string? defaultFileName = null)
    {
        var window = _host.Window;
        if (window is null)
        {
            return null;
        }

        var filters = new (string, string[])[]
        {
            ("SQL files", ["sql"]),
            ("All files", ["*"])
        };

        return await window.ShowSaveFileAsync(
            "Save SQL script",
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
            filters,
            defaultFileName ?? "query.sql");
    }

    public async Task<string?> SaveCsvFileAsync(string? defaultFileName = null)
    {
        var window = _host.Window;
        if (window is null)
        {
            return null;
        }

        var filters = new (string, string[])[]
        {
            ("CSV files", ["csv"]),
            ("All files", ["*"])
        };

        return await window.ShowSaveFileAsync(
            "Export results",
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
            filters,
            defaultFileName ?? "results.csv");
    }

    public async Task<string?> SaveJsonFileAsync(string? defaultFileName = null)
    {
        var window = _host.Window;
        if (window is null)
        {
            return null;
        }

        var filters = new (string, string[])[]
        {
            ("JSON files", ["json"]),
            ("All files", ["*"])
        };

        return await window.ShowSaveFileAsync(
            "Export results",
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
            filters,
            defaultFileName ?? "results.json");
    }
}
