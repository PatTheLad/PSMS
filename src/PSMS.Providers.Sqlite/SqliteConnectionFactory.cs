using Microsoft.Data.Sqlite;
using PSMS.Core.Models;

namespace PSMS.Providers.Sqlite;

internal static class SqliteConnectionFactory
{
    public static string ResolveFilePath(ConnectionDefinition connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.Database))
        {
            return connection.Database.Trim();
        }

        if (!string.IsNullOrWhiteSpace(connection.Server))
        {
            return connection.Server.Trim();
        }

        throw new InvalidOperationException("SQLite connection requires a file path in Database (preferred) or Server.");
    }

    public static string DatabaseDisplayName(ConnectionDefinition connection)
    {
        var path = ResolveFilePath(connection);
        var fileName = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(fileName) ? "main" : fileName;
    }

    public static SqliteConnection Create(ConnectionDefinition connection)
    {
        var path = ResolveFilePath(connection);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate
        };

        return new SqliteConnection(builder.ConnectionString);
    }
}
