using System.Diagnostics;
using Microsoft.Data.SqlClient;
using PSMS.Core.Abstractions;
using PSMS.Core.Models;
using PSMS.Core.Models.Admin;

namespace PSMS.Providers.SqlServer;

public sealed partial class SqlServerAdminService
{
    public async Task<IReadOnlyList<BlockingSessionInfo>> GetBlockingSessionsAsync(
        ConnectionDefinition connection,
        string? password,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                r.session_id,
                r.blocking_session_id,
                ISNULL(s.login_name, N''),
                ISNULL(DB_NAME(r.database_id), N''),
                ISNULL(r.wait_type, N''),
                r.wait_time,
                SUBSTRING(t.text, (r.statement_start_offset / 2) + 1,
                    ((CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(t.text) ELSE r.statement_end_offset END
                      - r.statement_start_offset) / 2) + 1)
            FROM sys.dm_exec_requests r
            LEFT JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id
            OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
            WHERE r.blocking_session_id <> 0
            ORDER BY r.wait_time DESC;
            """;
        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var list = new List<BlockingSessionInfo>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(new BlockingSessionInfo
                {
                    SessionId = reader.GetInt32(0),
                    BlockingSessionId = reader.GetInt32(1),
                    LoginName = reader.GetString(2),
                    Database = reader.GetString(3),
                    WaitType = reader.GetString(4),
                    WaitTimeMs = reader.GetInt32(5),
                    SqlText = reader.IsDBNull(6) ? null : reader.GetString(6)
                });
            }

            return list;
        }
        catch
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<ExpensiveQueryInfo>> GetExpensiveQueriesAsync(
        ConnectionDefinition connection,
        string? password,
        int top = 25,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(top, 1, 100);
        var sql = $"""
            SELECT TOP ({take})
                CONVERT(bigint, qs.query_hash),
                qs.execution_count,
                qs.total_worker_time / 1000.0,
                qs.total_elapsed_time / 1000.0,
                qs.total_logical_reads * 1.0,
                SUBSTRING(st.text, (qs.statement_start_offset / 2) + 1,
                    ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text) ELSE qs.statement_end_offset END
                      - qs.statement_start_offset) / 2) + 1)
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
            ORDER BY qs.total_worker_time DESC;
            """;
        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var list = new List<ExpensiveQueryInfo>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(new ExpensiveQueryInfo
                {
                    QueryHash = Convert.ToInt64(reader.GetValue(0)),
                    ExecutionCount = Convert.ToInt64(reader.GetValue(1)),
                    TotalCpuMs = Convert.ToDouble(reader.GetValue(2)),
                    TotalDurationMs = Convert.ToDouble(reader.GetValue(3)),
                    TotalLogicalReads = Convert.ToDouble(reader.GetValue(4)),
                    SqlText = reader.IsDBNull(5) ? null : reader.GetString(5)
                });
            }

            return list;
        }
        catch
        {
            return [];
        }
    }

    public async Task<AdminOperationResult> KillSessionAsync(
        ConnectionDefinition connection,
        string? password,
        int sessionId,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (sessionId <= 0)
        {
            return AdminOperationResult.Fail("Invalid session id.");
        }

        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand($"KILL {sessionId};", conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return AdminOperationResult.Ok($"Session {sessionId} killed.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public async Task<IReadOnlyList<LoginInfo>> GetLoginsAsync(
        ConnectionDefinition connection,
        string? password,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                name,
                type_desc,
                is_disabled,
                create_date,
                ISNULL(default_database_name, N'master')
            FROM sys.server_principals
            WHERE type IN ('S', 'U', 'G')
              AND name NOT LIKE N'##%'
            ORDER BY name;
            """;
        await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<LoginInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new LoginInfo
            {
                Name = reader.GetString(0),
                Type = reader.GetString(1),
                IsDisabled = reader.GetBoolean(2),
                CreateDate = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Local)),
                DefaultDatabase = reader.GetString(4)
            });
        }

        return list;
    }

    public async Task<AdminOperationResult> CreateLoginAsync(
        ConnectionDefinition connection,
        string? password,
        CreateLoginRequest request,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(request.LoginName) || string.IsNullOrWhiteSpace(request.Password))
        {
            return AdminOperationResult.Fail("Login name and password are required.");
        }

        if (!IsSafeIdentifier(request.LoginName))
        {
            return AdminOperationResult.Fail("Login name contains invalid characters.");
        }

        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            var checkPolicy = request.MustChange ? "ON" : "OFF";
            var sql = $"""
                CREATE LOGIN {QuoteIdent(request.LoginName)}
                WITH PASSWORD = N'{EscapeLiteral(request.Password)}' MUST_CHANGE,
                     DEFAULT_DATABASE = {QuoteIdent(string.IsNullOrWhiteSpace(request.DefaultDatabase) ? "master" : request.DefaultDatabase)},
                     CHECK_POLICY = {checkPolicy};
                """;
            if (!request.MustChange)
            {
                sql = $"""
                    CREATE LOGIN {QuoteIdent(request.LoginName)}
                    WITH PASSWORD = N'{EscapeLiteral(request.Password)}',
                         DEFAULT_DATABASE = {QuoteIdent(string.IsNullOrWhiteSpace(request.DefaultDatabase) ? "master" : request.DefaultDatabase)},
                         CHECK_POLICY = OFF;
                    """;
            }

            await using (var cmd = new SqlCommand(sql, conn))
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(request.MapToDatabase) && IsSafeIdentifier(request.MapToDatabase))
            {
                var user = string.IsNullOrWhiteSpace(request.DatabaseUserName) ? request.LoginName : request.DatabaseUserName!;
                var map = $"""
                    USE {QuoteIdent(request.MapToDatabase)};
                    IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'{EscapeLiteral(user)}')
                        CREATE USER {QuoteIdent(user)} FOR LOGIN {QuoteIdent(request.LoginName)};
                    """;
                await using var mapCmd = new SqlCommand(map, conn);
                await mapCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            sw.Stop();
            return AdminOperationResult.Ok($"Login '{request.LoginName}' created.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public async Task<AdminOperationResult> DropLoginAsync(
        ConnectionDefinition connection,
        string? password,
        string loginName,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (!IsSafeIdentifier(loginName))
        {
            return AdminOperationResult.Fail("Invalid login name.");
        }

        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand($"DROP LOGIN {QuoteIdent(loginName)};", conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return AdminOperationResult.Ok($"Login '{loginName}' dropped.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public async Task<AdminOperationResult> SetLoginEnabledAsync(
        ConnectionDefinition connection,
        string? password,
        string loginName,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (!IsSafeIdentifier(loginName))
        {
            return AdminOperationResult.Fail("Invalid login name.");
        }

        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            var verb = enabled ? "ENABLE" : "DISABLE";
            await using var cmd = new SqlCommand($"ALTER LOGIN {QuoteIdent(loginName)} {verb};", conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return AdminOperationResult.Ok($"Login '{loginName}' {verb.ToLowerInvariant()}d.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public async Task<IReadOnlyList<MissingIndexInfo>> GetMissingIndexesAsync(
        ConnectionDefinition connection,
        string? password,
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (50)
                DB_NAME(mid.database_id),
                OBJECT_SCHEMA_NAME(mid.object_id, mid.database_id),
                OBJECT_NAME(mid.object_id, mid.database_id),
                ISNULL(mid.equality_columns, N''),
                ISNULL(mid.inequality_columns, N''),
                ISNULL(mid.included_columns, N''),
                migs.avg_user_impact,
                migs.user_seeks,
                N'CREATE INDEX IX_auto ON '
                    + QUOTENAME(OBJECT_SCHEMA_NAME(mid.object_id, mid.database_id))
                    + N'.' + QUOTENAME(OBJECT_NAME(mid.object_id, mid.database_id))
                    + N' (' + ISNULL(mid.equality_columns, N'')
                    + CASE WHEN mid.equality_columns IS NOT NULL AND mid.inequality_columns IS NOT NULL THEN N', ' ELSE N'' END
                    + ISNULL(mid.inequality_columns, N'') + N')'
                    + CASE WHEN mid.included_columns IS NOT NULL THEN N' INCLUDE (' + mid.included_columns + N')' ELSE N'' END
                    + N';'
            FROM sys.dm_db_missing_index_details mid
            INNER JOIN sys.dm_db_missing_index_groups mig ON mig.index_handle = mid.index_handle
            INNER JOIN sys.dm_db_missing_index_group_stats migs ON migs.group_handle = mig.index_group_handle
            WHERE (@db IS NULL OR DB_NAME(mid.database_id) = @db)
            ORDER BY migs.avg_user_impact * migs.user_seeks DESC;
            """;
        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@db", (object?)database ?? DBNull.Value);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var list = new List<MissingIndexInfo>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(new MissingIndexInfo
                {
                    DatabaseName = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    SchemaName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    TableName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    EqualityColumns = reader.GetString(3),
                    InequalityColumns = reader.GetString(4),
                    IncludedColumns = reader.GetString(5),
                    Impact = Convert.ToDouble(reader.GetValue(6)),
                    UserSeeks = Convert.ToInt64(reader.GetValue(7)),
                    CreateIndexStatement = reader.GetString(8)
                });
            }

            return list;
        }
        catch
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<IndexFragmentationInfo>> GetIndexFragmentationAsync(
        ConnectionDefinition connection,
        string? password,
        string database,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeIdentifier(database))
        {
            return [];
        }

        var sql = $"""
            SELECT TOP (100)
                OBJECT_SCHEMA_NAME(ips.object_id),
                OBJECT_NAME(ips.object_id),
                i.name,
                ips.avg_fragmentation_in_percent,
                ips.page_count,
                ips.index_type_desc
            FROM sys.dm_db_index_physical_stats(DB_ID(N'{EscapeLiteral(database)}'), NULL, NULL, NULL, N'LIMITED') ips
            INNER JOIN sys.indexes i ON i.object_id = ips.object_id AND i.index_id = ips.index_id
            WHERE ips.avg_fragmentation_in_percent > 5
              AND ips.page_count > 100
              AND i.name IS NOT NULL
            ORDER BY ips.avg_fragmentation_in_percent DESC;
            """;
        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, database);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 180 };
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var list = new List<IndexFragmentationInfo>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(new IndexFragmentationInfo
                {
                    SchemaName = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    TableName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    IndexName = reader.GetString(2),
                    AvgFragmentationPercent = Convert.ToDouble(reader.GetValue(3)),
                    PageCount = Convert.ToInt64(reader.GetValue(4)),
                    IndexType = reader.GetString(5)
                });
            }

            return list;
        }
        catch
        {
            return [];
        }
    }

    public async Task<AdminOperationResult> RebuildIndexAsync(
        ConnectionDefinition connection,
        string? password,
        string database,
        string schema,
        string table,
        string indexName,
        CancellationToken cancellationToken = default)
        => await RunIndexOpAsync(connection, password, database, schema, table, indexName, "REBUILD", cancellationToken)
            .ConfigureAwait(false);

    public async Task<AdminOperationResult> ReorganizeIndexAsync(
        ConnectionDefinition connection,
        string? password,
        string database,
        string schema,
        string table,
        string indexName,
        CancellationToken cancellationToken = default)
        => await RunIndexOpAsync(connection, password, database, schema, table, indexName, "REORGANIZE", cancellationToken)
            .ConfigureAwait(false);

    public async Task<DatabasePropertiesInfo?> GetDatabasePropertiesAsync(
        ConnectionDefinition connection,
        string? password,
        string database,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeIdentifier(database))
        {
            return null;
        }

        const string meta = """
            SELECT
                d.name,
                d.state_desc,
                d.recovery_model_desc,
                ISNULL(d.collation_name, N''),
                d.compatibility_level,
                d.is_read_only,
                d.is_auto_close_on,
                d.is_auto_shrink_on
            FROM sys.databases d
            WHERE d.name = @db;
            """;
        const string files = """
            SELECT
                mf.name,
                mf.physical_name,
                mf.type_desc,
                mf.size * 8.0 / 1024,
                FILEPROPERTY(mf.name, 'SpaceUsed') * 8.0 / 1024,
                mf.file_id
            FROM sys.master_files mf
            INNER JOIN sys.databases d ON d.database_id = mf.database_id
            WHERE d.name = @db
            ORDER BY mf.file_id;
            """;

        await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        DatabasePropertiesInfo? info = null;
        await using (var cmd = new SqlCommand(meta, conn))
        {
            cmd.Parameters.AddWithValue("@db", database);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                info = new DatabasePropertiesInfo
                {
                    Name = reader.GetString(0),
                    State = reader.GetString(1),
                    RecoveryModel = reader.GetString(2),
                    Collation = reader.GetString(3),
                    CompatibilityLevel = reader.GetByte(4),
                    IsReadOnly = reader.GetBoolean(5),
                    AutoClose = reader.GetBoolean(6),
                    AutoShrink = reader.GetBoolean(7)
                };
            }
        }

        if (info is null)
        {
            return null;
        }

        var fileList = new List<DatabaseFileInfo>();
        await using (var cmd = new SqlCommand(files, conn))
        {
            cmd.Parameters.AddWithValue("@db", database);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                fileList.Add(new DatabaseFileInfo
                {
                    LogicalName = reader.GetString(0),
                    PhysicalName = reader.GetString(1),
                    Type = reader.GetString(2),
                    SizeMb = Convert.ToDouble(reader.GetValue(3)),
                    UsedMb = reader.IsDBNull(4) ? 0 : Convert.ToDouble(reader.GetValue(4)),
                    FileId = reader.GetInt32(5)
                });
            }
        }

        return new DatabasePropertiesInfo
        {
            Name = info.Name,
            State = info.State,
            RecoveryModel = info.RecoveryModel,
            Collation = info.Collation,
            CompatibilityLevel = info.CompatibilityLevel,
            IsReadOnly = info.IsReadOnly,
            AutoClose = info.AutoClose,
            AutoShrink = info.AutoShrink,
            Files = fileList
        };
    }

    public async Task<AdminOperationResult> SetDatabaseOnlineAsync(
        ConnectionDefinition connection,
        string? password,
        string database,
        bool online,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (!IsSafeIdentifier(database))
        {
            return AdminOperationResult.Fail("Invalid database name.");
        }

        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            var state = online ? "ONLINE" : "OFFLINE WITH ROLLBACK IMMEDIATE";
            await using var cmd = new SqlCommand($"ALTER DATABASE {QuoteIdent(database)} SET {state};", conn) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return AdminOperationResult.Ok($"Database '{database}' set {state.Split(' ')[0]}.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public async Task<AdminOperationResult> SetDatabaseReadOnlyAsync(
        ConnectionDefinition connection,
        string? password,
        string database,
        bool readOnly,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (!IsSafeIdentifier(database))
        {
            return AdminOperationResult.Fail("Invalid database name.");
        }

        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            var mode = readOnly ? "READ_ONLY" : "READ_WRITE";
            await using var cmd = new SqlCommand($"ALTER DATABASE {QuoteIdent(database)} SET {mode};", conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return AdminOperationResult.Ok($"Database '{database}' set {mode}.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public async Task<AdminOperationResult> ShrinkDatabaseAsync(
        ConnectionDefinition connection,
        string? password,
        string database,
        int targetPercentFree = 10,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (!IsSafeIdentifier(database))
        {
            return AdminOperationResult.Fail("Invalid database name.");
        }

        var pct = Math.Clamp(targetPercentFree, 0, 99);
        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand($"DBCC SHRINKDATABASE ({QuoteIdent(database)}, {pct});", conn) { CommandTimeout = 600 };
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return AdminOperationResult.Ok($"Shrink requested for '{database}'.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    private static async Task<AdminOperationResult> RunIndexOpAsync(
        ConnectionDefinition connection,
        string? password,
        string database,
        string schema,
        string table,
        string indexName,
        string op,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        if (!IsSafeIdentifier(database) || !IsSafeIdentifier(schema) || !IsSafeIdentifier(table) || !IsSafeIdentifier(indexName))
        {
            return AdminOperationResult.Fail("Invalid identifier.");
        }

        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, database);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            var sql = $"ALTER INDEX {QuoteIdent(indexName)} ON {QuoteIdent(schema)}.{QuoteIdent(table)} {op};";
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 600 };
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return AdminOperationResult.Ok($"Index {op.ToLowerInvariant()} completed.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }
}
