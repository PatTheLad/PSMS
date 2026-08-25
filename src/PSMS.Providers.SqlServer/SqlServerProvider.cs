using System.Diagnostics;
using System.Text;
using Microsoft.Data.SqlClient;
using PSMS.Core.Abstractions;
using PSMS.Core.Models;

namespace PSMS.Providers.SqlServer;

public sealed class SqlServerProvider : IDbProvider
{
    public DbEngine Engine => DbEngine.SqlServer;

    public async Task TestConnectionAsync(ConnectionDefinition connection, string? password, CancellationToken cancellationToken = default)
    {
        await using var sqlConnection = CreateConnection(connection, password, databaseOverride: connection.Database);
        await sqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DatabaseInfo>> GetDatabasesAsync(ConnectionDefinition connection, string? password, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT name
            FROM sys.databases
            WHERE state = 0
            ORDER BY name;
            """;

        await using var sqlConnection = CreateConnection(connection, password, databaseOverride: "master");
        await sqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, sqlConnection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<DatabaseInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new DatabaseInfo(reader.GetString(0)));
        }

        return results;
    }

    public async Task<IReadOnlyList<SchemaInfo>> GetSchemasAsync(ConnectionDefinition connection, string? password, string database, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT DISTINCT s.name
            FROM sys.schemas s
            WHERE EXISTS (
                SELECT 1 FROM sys.objects o
                WHERE o.schema_id = s.schema_id
                  AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF')
            )
            ORDER BY s.name;
            """;

        return await QueryStringsAsync(connection, password, database, sql, cancellationToken,
            name => new SchemaInfo(name)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TableInfo>> GetTablesAsync(ConnectionDefinition connection, string? password, string database, string schema, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TABLE_SCHEMA, TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_TYPE = 'BASE TABLE'
              AND TABLE_SCHEMA = @schema
            ORDER BY TABLE_NAME;
            """;

        return await QueryTwoStringAsync(connection, password, database, sql, schema, cancellationToken,
            (s, n) => new TableInfo(s, n)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ViewInfo>> GetViewsAsync(ConnectionDefinition connection, string? password, string database, string schema, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TABLE_SCHEMA, TABLE_NAME
            FROM INFORMATION_SCHEMA.VIEWS
            WHERE TABLE_SCHEMA = @schema
            ORDER BY TABLE_NAME;
            """;

        return await QueryTwoStringAsync(connection, password, database, sql, schema, cancellationToken,
            (s, n) => new ViewInfo(s, n)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ConnectionDefinition connection, string? password, string database, string schema, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT ROUTINE_SCHEMA, ROUTINE_NAME
            FROM INFORMATION_SCHEMA.ROUTINES
            WHERE ROUTINE_TYPE = 'PROCEDURE'
              AND ROUTINE_SCHEMA = @schema
            ORDER BY ROUTINE_NAME;
            """;

        return await QueryTwoStringAsync(connection, password, database, sql, schema, cancellationToken,
            (s, n) => new ProcedureInfo(s, n)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FunctionInfo>> GetFunctionsAsync(ConnectionDefinition connection, string? password, string database, string schema, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT ROUTINE_SCHEMA, ROUTINE_NAME
            FROM INFORMATION_SCHEMA.ROUTINES
            WHERE ROUTINE_TYPE = 'FUNCTION'
              AND ROUTINE_SCHEMA = @schema
            ORDER BY ROUTINE_NAME;
            """;

        return await QueryTwoStringAsync(connection, password, database, sql, schema, cancellationToken,
            (s, n) => new FunctionInfo(s, n)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(ConnectionDefinition connection, string? password, string database, string schema, string table, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                TABLE_SCHEMA,
                TABLE_NAME,
                COLUMN_NAME,
                DATA_TYPE,
                CASE WHEN IS_NULLABLE = 'YES' THEN 1 ELSE 0 END,
                CHARACTER_MAXIMUM_LENGTH,
                ORDINAL_POSITION
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @schema
              AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION;
            """;

        await using var sqlConnection = CreateConnection(connection, password, database);
        await sqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, sqlConnection);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<ColumnInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ColumnInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4) == 1,
                reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5)),
                reader.GetInt32(6)));
        }

        return results;
    }

    public async Task<IReadOnlyList<CatalogObjectInfo>> GetCatalogObjectsAsync(ConnectionDefinition connection, string? password, string database, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT s.name AS schema_name, o.name AS object_name, o.type
            FROM sys.objects o
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE o.is_ms_shipped = 0
              AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF')
            ORDER BY s.name, o.name;
            """;

        await using var sqlConnection = CreateConnection(connection, password, database);
        await sqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, sqlConnection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<CatalogObjectInfo>();
        var schemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            var type = reader.GetString(2).Trim();
            schemas.Add(schema);

            var kind = type switch
            {
                "U" => CatalogObjectKind.Table,
                "V" => CatalogObjectKind.View,
                "P" => CatalogObjectKind.Procedure,
                "FN" or "IF" or "TF" => CatalogObjectKind.Function,
                _ => CatalogObjectKind.Table
            };

            results.Add(new CatalogObjectInfo(schema, name, kind));
        }

        var schemaSymbols = schemas
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .Select(s => new CatalogObjectInfo(s, s, CatalogObjectKind.Schema))
            .ToList();

        return schemaSymbols.Concat(results).ToList();
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetAllColumnsAsync(ConnectionDefinition connection, string? password, string database, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                TABLE_SCHEMA,
                TABLE_NAME,
                COLUMN_NAME,
                DATA_TYPE,
                CASE WHEN IS_NULLABLE = 'YES' THEN 1 ELSE 0 END,
                CHARACTER_MAXIMUM_LENGTH,
                ORDINAL_POSITION
            FROM INFORMATION_SCHEMA.COLUMNS
            ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION;
            """;

        await using var sqlConnection = CreateConnection(connection, password, database);
        await sqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, sqlConnection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<ColumnInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ColumnInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4) == 1,
                reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5)),
                reader.GetInt32(6)));
        }

        return results;
    }

    public async Task<string?> GetObjectDefinitionAsync(ConnectionDefinition connection, string? password, string database, string schema, string name, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT OBJECT_DEFINITION(OBJECT_ID(@qualified));
            """;

        await using var sqlConnection = CreateConnection(connection, password, database);
        await sqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, sqlConnection);
        command.Parameters.AddWithValue("@qualified", $"[{schema}].[{name}]");
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is string s ? s : result?.ToString();
    }

    public async Task<string> ScriptCreateTableAsync(ConnectionDefinition connection, string? password, string database, string schema, string table, CancellationToken cancellationToken = default)
    {
        var columns = await GetColumnsAsync(connection, password, database, schema, table, cancellationToken).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE [{schema}].[{table}] (");
        for (var i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            var type = FormatSqlType(col);
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
            await using var sqlConnection = CreateConnection(connection, password, database);
            sqlConnection.InfoMessage += (_, e) => messages.Add(e.Message);
            await sqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = new SqlCommand(sql, sqlConnection)
            {
                CommandTimeout = 0
            };

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
                        row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }

                    rows.Add(row);
                }

                if (truncated)
                {
                    messages.Add($"Result set {resultSets.Count + 1}: truncated at {maxRows:N0} rows.");
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        // drain remaining rows in this result
                    }
                }

                resultSets.Add(new ResultSet
                {
                    Columns = columns,
                    Rows = rows,
                    Truncated = truncated
                });
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

    private static string FormatSqlType(ColumnInfo col)
    {
        return col.DataType.ToLowerInvariant() switch
        {
            "nvarchar" or "varchar" or "nchar" or "char" or "varbinary" or "binary"
                => col.MaxLength is null or < 0 ? $"{col.DataType}(MAX)" : $"{col.DataType}({col.MaxLength})",
            _ => col.DataType
        };
    }

    private static SqlConnection CreateConnection(ConnectionDefinition connection, string? password, string? databaseOverride)
        => SqlServerConnectionFactory.Create(connection, password, databaseOverride);

    private static async Task<IReadOnlyList<T>> QueryStringsAsync<T>(
        ConnectionDefinition connection,
        string? password,
        string database,
        string sql,
        CancellationToken cancellationToken,
        Func<string, T> map)
    {
        await using var sqlConnection = CreateConnection(connection, password, database);
        await sqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, sqlConnection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(map(reader.GetString(0)));
        }

        return results;
    }

    private static async Task<IReadOnlyList<T>> QueryTwoStringAsync<T>(
        ConnectionDefinition connection,
        string? password,
        string database,
        string sql,
        string schema,
        CancellationToken cancellationToken,
        Func<string, string, T> map)
    {
        await using var sqlConnection = CreateConnection(connection, password, database);
        await sqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, sqlConnection);
        command.Parameters.AddWithValue("@schema", schema);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(map(reader.GetString(0), reader.GetString(1)));
        }

        return results;
    }
}
