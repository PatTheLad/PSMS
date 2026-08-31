using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using PSMS.Core.Abstractions;
using PSMS.Core.Models;
using PSMS.Core.Sql;

namespace PSMS.Providers.Sqlite;

public sealed class SqliteProvider : IDbProvider
{
    private const string MainSchema = "main";

    public DbEngine Engine => DbEngine.Sqlite;

    public async Task TestConnectionAsync(ConnectionDefinition connection, string? password, CancellationToken cancellationToken = default)
    {
        await using var db = SqliteConnectionFactory.Create(connection);
        await db.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<DatabaseInfo>> GetDatabasesAsync(ConnectionDefinition connection, string? password, CancellationToken cancellationToken = default)
    {
        var name = SqliteConnectionFactory.DatabaseDisplayName(connection);
        IReadOnlyList<DatabaseInfo> results = [new DatabaseInfo(name)];
        return Task.FromResult(results);
    }

    public Task<IReadOnlyList<SchemaInfo>> GetSchemasAsync(ConnectionDefinition connection, string? password, string database, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SchemaInfo> results = [new SchemaInfo(MainSchema)];
        return Task.FromResult(results);
    }

    public async Task<IReadOnlyList<TableInfo>> GetTablesAsync(ConnectionDefinition connection, string? password, string database, string schema, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;

        await using var db = SqliteConnectionFactory.Create(connection);
        await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<TableInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new TableInfo(MainSchema, reader.GetString(0)));
        }

        return results;
    }

    public async Task<IReadOnlyList<ViewInfo>> GetViewsAsync(ConnectionDefinition connection, string? password, string database, string schema, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'view'
            ORDER BY name;
            """;

        await using var db = SqliteConnectionFactory.Create(connection);
        await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<ViewInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ViewInfo(MainSchema, reader.GetString(0)));
        }

        return results;
    }

    public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ConnectionDefinition connection, string? password, string database, string schema, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ProcedureInfo>>([]);

    public Task<IReadOnlyList<FunctionInfo>> GetFunctionsAsync(ConnectionDefinition connection, string? password, string database, string schema, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<FunctionInfo>>([]);

    public async Task<TableSchemaOverview> GetTableSchemaOverviewAsync(
        ConnectionDefinition connection,
        string? password,
        string database,
        string schema,
        string table,
        CancellationToken cancellationToken = default)
    {
        var columns = await GetColumnsAsync(connection, password, database, schema, table, cancellationToken).ConfigureAwait(false);
        return new TableSchemaOverview { Columns = columns };
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(ConnectionDefinition connection, string? password, string database, string schema, string table, CancellationToken cancellationToken = default)
    {
        await using var db = SqliteConnectionFactory.Create(connection);
        await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadTableInfoAsync(db, table, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CatalogObjectInfo>> GetCatalogObjectsAsync(ConnectionDefinition connection, string? password, string database, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT name, type
            FROM sqlite_master
            WHERE type IN ('table', 'view')
              AND name NOT LIKE 'sqlite_%'
            ORDER BY type, name;
            """;

        await using var db = SqliteConnectionFactory.Create(connection);
        await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<CatalogObjectInfo>
        {
            new(MainSchema, MainSchema, CatalogObjectKind.Schema)
        };

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            var type = reader.GetString(1);
            var kind = string.Equals(type, "view", StringComparison.OrdinalIgnoreCase)
                ? CatalogObjectKind.View
                : CatalogObjectKind.Table;
            results.Add(new CatalogObjectInfo(MainSchema, name, kind));
        }

        return results;
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetAllColumnsAsync(ConnectionDefinition connection, string? password, string database, CancellationToken cancellationToken = default)
    {
        var tables = await GetTablesAsync(connection, password, database, MainSchema, cancellationToken).ConfigureAwait(false);
        var views = await GetViewsAsync(connection, password, database, MainSchema, cancellationToken).ConfigureAwait(false);

        await using var db = SqliteConnectionFactory.Create(connection);
        await db.OpenAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<ColumnInfo>();
        foreach (var table in tables.Concat(views.Select(v => new TableInfo(v.Schema, v.Name))))
        {
            var columns = await ReadTableInfoAsync(db, table.Name, cancellationToken).ConfigureAwait(false);
            results.AddRange(columns);
        }

        return results;
    }

    public async Task<string?> GetObjectDefinitionAsync(ConnectionDefinition connection, string? password, string database, string schema, string name, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT sql
            FROM sqlite_master
            WHERE name = $name
            LIMIT 1;
            """;

        await using var db = SqliteConnectionFactory.Create(connection);
        await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$name", name);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is string s ? s : result?.ToString();
    }

    public async Task<string> ScriptCreateTableAsync(ConnectionDefinition connection, string? password, string database, string schema, string table, CancellationToken cancellationToken = default)
    {
        var definition = await GetObjectDefinitionAsync(connection, password, database, schema, table, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(definition))
        {
            return definition.EndsWith(';') ? definition : definition + ";";
        }

        var columns = await GetColumnsAsync(connection, password, database, schema, table, cancellationToken).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE \"{table}\" (");
        for (var i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            var nullability = col.IsNullable ? "NULL" : "NOT NULL";
            var comma = i < columns.Count - 1 ? "," : string.Empty;
            sb.AppendLine($"    \"{col.Name}\" {col.DataType} {nullability}{comma}");
        }

        sb.AppendLine(");");
        return sb.ToString();
    }

    public async Task<QueryResult> ExecuteQueryAsync(
        ConnectionDefinition connection,
        string? password,
        string database,
        string sql,
        int maxRows = 10_000,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var messages = new List<string>();
        var resultSets = new List<ResultSet>();
        var rowsAffected = 0;

        try
        {
            await using var db = SqliteConnectionFactory.Create(connection);
            await db.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = db.CreateCommand();
            command.CommandText = sql;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            do
            {
                if (reader.FieldCount <= 0)
                {
                    continue;
                }

                var columns = new string[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    columns[i] = reader.GetName(i);
                }

                var rows = new List<IReadOnlyList<object?>>();
                var displayRows = new List<IReadOnlyList<string?>>();
                var truncated = false;
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (rows.Count >= maxRows)
                    {
                        truncated = true;
                        break;
                    }

                    var row = new object?[reader.FieldCount];
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        row[i] = ResultMaterializer.ReadCell(reader, i);
                    }

                    rows.Add(row);
                    displayRows.Add(ResultMaterializer.FormatRow(row));
                }

                if (truncated)
                {
                    messages.Add($"Result set {resultSets.Count + 1}: truncated at {maxRows:N0} rows (remaining rows not fetched).");
                    try
                    {
                        command.Cancel();
                    }
                    catch
                    {
                        // ignored
                    }
                }

                resultSets.Add(new ResultSet
                {
                    Columns = columns,
                    Rows = rows,
                    DisplayRows = displayRows,
                    Truncated = truncated
                });

                if (truncated)
                {
                    break;
                }
            }
            while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

            rowsAffected = reader.RecordsAffected;
            sw.Stop();

            if (resultSets.Count > 0)
            {
                var totalRows = resultSets.Sum(r => r.Rows.Count);
                messages.Insert(0, $"Completed in {sw.ElapsedMilliseconds} ms. {resultSets.Count} result set(s), {totalRows:N0} row(s).");
            }
            else
            {
                messages.Insert(0, $"Completed in {sw.ElapsedMilliseconds} ms. {rowsAffected} row(s) affected.");
            }

            return new QueryResult
            {
                ResultSets = resultSets,
                Messages = messages,
                ElapsedMilliseconds = sw.ElapsedMilliseconds,
                RowsAffected = rowsAffected
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new QueryResult
            {
                ResultSets = resultSets,
                Messages = messages.Concat(["Query cancelled."]).ToList(),
                ElapsedMilliseconds = sw.ElapsedMilliseconds,
                Error = "Cancelled"
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new QueryResult
            {
                ResultSets = resultSets,
                Messages = messages.Concat([ex.Message]).ToList(),
                ElapsedMilliseconds = sw.ElapsedMilliseconds,
                Error = ex.Message
            };
        }
    }

    public Task<QueryResult> ExecuteEstimatedPlanAsync(
        ConnectionDefinition connection,
        string? password,
        string database,
        string sql,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new QueryResult
        {
            Error = "Estimated execution plan is only available for SQL Server.",
            Messages = ["Estimated execution plan is only available for SQL Server."]
        });

    public Task<QueryResult> ExecuteActualPlanAsync(
        ConnectionDefinition connection,
        string? password,
        string database,
        string sql,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new QueryResult
        {
            Error = "Actual execution plan is only available for SQL Server.",
            Messages = ["Actual execution plan is only available for SQL Server."]
        });

    private static async Task<IReadOnlyList<ColumnInfo>> ReadTableInfoAsync(SqliteConnection db, string table, CancellationToken cancellationToken)
    {
        // PRAGMA table_info does not accept parameters for the table name.
        await using var command = db.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdent(table)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<ColumnInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // cid, name, type, notnull, dflt_value, pk
            var ordinal = reader.GetInt32(0);
            var name = reader.GetString(1);
            var dataType = reader.IsDBNull(2) || string.IsNullOrWhiteSpace(reader.GetString(2))
                ? "BLOB"
                : reader.GetString(2);
            var notNull = reader.GetInt32(3) == 1;
            var (baseType, maxLength) = ParseSqliteType(dataType);

            results.Add(new ColumnInfo(
                MainSchema,
                table,
                name,
                baseType,
                !notNull,
                maxLength,
                ordinal + 1));
        }

        return results;
    }

    private static (string Type, int? MaxLength) ParseSqliteType(string declared)
    {
        var open = declared.IndexOf('(');
        if (open < 0)
        {
            return (declared, null);
        }

        var close = declared.IndexOf(')', open + 1);
        if (close < 0)
        {
            return (declared, null);
        }

        var typeName = declared[..open].Trim();
        var lengthPart = declared[(open + 1)..close].Split(',')[0].Trim();
        return int.TryParse(lengthPart, out var length) ? (typeName, length) : (typeName, null);
    }

    private static string QuoteIdent(string name)
        => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
