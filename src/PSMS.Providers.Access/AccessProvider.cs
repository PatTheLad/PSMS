using System.Data;
using System.Data.Odbc;
using System.Diagnostics;
using System.Text;
using PSMS.Core.Abstractions;
using PSMS.Core.Models;
using PSMS.Core.Sql;

namespace PSMS.Providers.Access;

public sealed class AccessProvider : IDbProvider
{
    private const string DefaultSchema = "dbo";

    public DbEngine Engine => DbEngine.Access;

    public async Task TestConnectionAsync(ConnectionDefinition connection, string? password, CancellationToken cancellationToken = default)
    {
        AccessConnectionFactory.EnsureWindows();
        await using var db = AccessConnectionFactory.Create(connection, password);
        await db.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<DatabaseInfo>> GetDatabasesAsync(ConnectionDefinition connection, string? password, CancellationToken cancellationToken = default)
    {
        AccessConnectionFactory.EnsureWindows();
        var name = AccessConnectionFactory.DatabaseDisplayName(connection);
        IReadOnlyList<DatabaseInfo> results = [new DatabaseInfo(name)];
        return Task.FromResult(results);
    }

    public Task<IReadOnlyList<SchemaInfo>> GetSchemasAsync(ConnectionDefinition connection, string? password, string database, CancellationToken cancellationToken = default)
    {
        AccessConnectionFactory.EnsureWindows();
        IReadOnlyList<SchemaInfo> results = [new SchemaInfo(DefaultSchema)];
        return Task.FromResult(results);
    }

    public async Task<IReadOnlyList<TableInfo>> GetTablesAsync(ConnectionDefinition connection, string? password, string database, string schema, CancellationToken cancellationToken = default)
    {
        AccessConnectionFactory.EnsureWindows();
        await using var db = AccessConnectionFactory.Create(connection, password);
        await db.OpenAsync(cancellationToken).ConfigureAwait(false);

        var tables = await GetSchemaTablesAsync(db, tableTypes: ["TABLE"], cancellationToken).ConfigureAwait(false);
        return tables
            .Select(t => new TableInfo(DefaultSchema, t))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<ViewInfo>> GetViewsAsync(ConnectionDefinition connection, string? password, string database, string schema, CancellationToken cancellationToken = default)
    {
        AccessConnectionFactory.EnsureWindows();
        await using var db = AccessConnectionFactory.Create(connection, password);
        await db.OpenAsync(cancellationToken).ConfigureAwait(false);

        var views = await GetSchemaTablesAsync(db, tableTypes: ["VIEW"], cancellationToken).ConfigureAwait(false);
        return views
            .Select(v => new ViewInfo(DefaultSchema, v))
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ConnectionDefinition connection, string? password, string database, string schema, CancellationToken cancellationToken = default)
    {
        AccessConnectionFactory.EnsureWindows();
        return Task.FromResult<IReadOnlyList<ProcedureInfo>>([]);
    }

    public Task<IReadOnlyList<FunctionInfo>> GetFunctionsAsync(ConnectionDefinition connection, string? password, string database, string schema, CancellationToken cancellationToken = default)
    {
        AccessConnectionFactory.EnsureWindows();
        return Task.FromResult<IReadOnlyList<FunctionInfo>>([]);
    }

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
        AccessConnectionFactory.EnsureWindows();
        await using var db = AccessConnectionFactory.Create(connection, password);
        await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadColumnsAsync(db, table, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CatalogObjectInfo>> GetCatalogObjectsAsync(ConnectionDefinition connection, string? password, string database, CancellationToken cancellationToken = default)
    {
        AccessConnectionFactory.EnsureWindows();
        await using var db = AccessConnectionFactory.Create(connection, password);
        await db.OpenAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<CatalogObjectInfo>
        {
            new(DefaultSchema, DefaultSchema, CatalogObjectKind.Schema)
        };

        var tables = await GetSchemaTablesAsync(db, tableTypes: ["TABLE"], cancellationToken).ConfigureAwait(false);
        foreach (var name in tables.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            results.Add(new CatalogObjectInfo(DefaultSchema, name, CatalogObjectKind.Table));
        }

        var views = await GetSchemaTablesAsync(db, tableTypes: ["VIEW"], cancellationToken).ConfigureAwait(false);
        foreach (var name in views.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            results.Add(new CatalogObjectInfo(DefaultSchema, name, CatalogObjectKind.View));
        }

        return results;
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetAllColumnsAsync(ConnectionDefinition connection, string? password, string database, CancellationToken cancellationToken = default)
    {
        AccessConnectionFactory.EnsureWindows();
        await using var db = AccessConnectionFactory.Create(connection, password);
        await db.OpenAsync(cancellationToken).ConfigureAwait(false);

        var tableNames = await GetSchemaTablesAsync(db, tableTypes: ["TABLE", "VIEW"], cancellationToken).ConfigureAwait(false);
        var results = new List<ColumnInfo>();
        foreach (var table in tableNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var columns = await ReadColumnsAsync(db, table, cancellationToken).ConfigureAwait(false);
            results.AddRange(columns);
        }

        return results;
    }

    public Task<string?> GetObjectDefinitionAsync(ConnectionDefinition connection, string? password, string database, string schema, string name, CancellationToken cancellationToken = default)
    {
        AccessConnectionFactory.EnsureWindows();
        return Task.FromResult<string?>(null);
    }

    public async Task<string> ScriptCreateTableAsync(ConnectionDefinition connection, string? password, string database, string schema, string table, CancellationToken cancellationToken = default)
    {
        AccessConnectionFactory.EnsureWindows();
        var columns = await GetColumnsAsync(connection, password, database, schema, table, cancellationToken).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE [{table}] (");
        for (var i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            var type = FormatAccessType(col);
            var nullability = col.IsNullable ? "NULL" : "NOT NULL";
            var comma = i < columns.Count - 1 ? "," : string.Empty;
            sb.AppendLine($"    [{col.Name}] {type} {nullability}{comma}");
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
            AccessConnectionFactory.EnsureWindows();
            await using var db = AccessConnectionFactory.Create(connection, password);
            await db.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = db.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 0;

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

    private static async Task<IReadOnlyList<string>> GetSchemaTablesAsync(
        OdbcConnection db,
        string[] tableTypes,
        CancellationToken cancellationToken)
    {
        // OdbcConnection.GetSchema is sync; wrap to keep call sites async-friendly.
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var schema = db.GetSchema("Tables");
            var names = new List<string>();
            foreach (DataRow row in schema.Rows)
            {
                var type = Convert.ToString(row["TABLE_TYPE"]) ?? string.Empty;
                var name = Convert.ToString(row["TABLE_NAME"]) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                // Skip Access system tables
                if (name.StartsWith("MSys", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (tableTypes.Any(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase)))
                {
                    names.Add(name);
                }
            }

            return (IReadOnlyList<string>)names;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ColumnInfo>> ReadColumnsAsync(
        OdbcConnection db,
        string table,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Try GetSchema("Columns") first; fall back to SELECT * WHERE 1=0.
            try
            {
                using var schema = db.GetSchema("Columns", [null, null, table, null]);
                if (schema.Rows.Count > 0)
                {
                    var fromSchema = new List<ColumnInfo>();
                    foreach (DataRow row in schema.Rows)
                    {
                        var name = Convert.ToString(row["COLUMN_NAME"]) ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        var dataType = Convert.ToString(row["TYPE_NAME"]) ?? "Unknown";
                        var ordinal = row.Table.Columns.Contains("ORDINAL_POSITION") && row["ORDINAL_POSITION"] is not DBNull
                            ? Convert.ToInt32(row["ORDINAL_POSITION"])
                            : fromSchema.Count + 1;
                        var nullable = !row.Table.Columns.Contains("NULLABLE")
                            || row["NULLABLE"] is DBNull
                            || Convert.ToInt32(row["NULLABLE"]) != 0;
                        int? maxLength = null;
                        if (row.Table.Columns.Contains("COLUMN_SIZE") && row["COLUMN_SIZE"] is not DBNull)
                        {
                            maxLength = Convert.ToInt32(row["COLUMN_SIZE"]);
                        }

                        fromSchema.Add(new ColumnInfo(DefaultSchema, table, name, dataType, nullable, maxLength, ordinal));
                    }

                    return (IReadOnlyList<ColumnInfo>)fromSchema
                        .OrderBy(c => c.OrdinalPosition)
                        .ToList();
                }
            }
            catch (OdbcException)
            {
                // fall through to reader-based discovery
            }

            using var command = db.CreateCommand();
            command.CommandText = $"SELECT * FROM [{table.Replace("]", "]]", StringComparison.Ordinal)}] WHERE 1=0";
            using var reader = command.ExecuteReader(CommandBehavior.SchemaOnly);
            var schemaTable = reader.GetSchemaTable();
            var results = new List<ColumnInfo>();
            if (schemaTable is null)
            {
                return (IReadOnlyList<ColumnInfo>)results;
            }

            foreach (DataRow row in schemaTable.Rows)
            {
                var name = Convert.ToString(row["ColumnName"]) ?? string.Empty;
                var dataType = row["DataType"] is Type t ? t.Name : "Unknown";
                var ordinal = Convert.ToInt32(row["ColumnOrdinal"]) + 1;
                var nullable = row["AllowDBNull"] is true or 1;
                int? maxLength = row["ColumnSize"] is not DBNull and not null
                    ? Convert.ToInt32(row["ColumnSize"])
                    : null;

                results.Add(new ColumnInfo(DefaultSchema, table, name, dataType, nullable, maxLength, ordinal));
            }

            return (IReadOnlyList<ColumnInfo>)results;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string FormatAccessType(ColumnInfo col)
    {
        return col.DataType.ToUpperInvariant() switch
        {
            "VARCHAR" or "CHAR" or "NVARCHAR" or "LONGCHAR" or "TEXT"
                => col.MaxLength is null or <= 0 or > 255
                    ? "MEMO"
                    : $"TEXT({col.MaxLength})",
            "INTEGER" or "INT" or "INT32" => "LONG",
            "INT16" or "SMALLINT" => "INTEGER",
            "INT64" or "BIGINT" or "LONG" => "LONG",
            "DOUBLE" or "FLOAT" or "SINGLE" => "DOUBLE",
            "DECIMAL" or "NUMERIC" or "CURRENCY" => "CURRENCY",
            "DATETIME" or "DATE" or "TIMESTAMP" => "DATETIME",
            "BOOLEAN" or "BIT" or "YESNO" => "YESNO",
            "BYTE[]" or "BINARY" or "VARBINARY" or "LONGBINARY" or "OLEOBJECT" => "OLEOBJECT",
            "GUID" or "UNIQUEIDENTIFIER" => "GUID",
            _ => col.DataType
        };
    }
}
