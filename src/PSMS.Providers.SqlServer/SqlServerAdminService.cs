using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using PSMS.Core.Abstractions;
using PSMS.Core.Models;
using PSMS.Core.Models.Admin;

namespace PSMS.Providers.SqlServer;

public sealed class SqlServerAdminService : ISqlServerAdminService
{
    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "master", "model", "msdb", "tempdb"
    };

    public async Task<IReadOnlyList<DatabaseAdminInfo>> GetDatabaseAdminInfoAsync(
        ConnectionDefinition connection,
        string? password,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                d.name,
                d.state_desc,
                d.recovery_model_desc,
                d.collation_name,
                CAST(ISNULL(SUM(CASE WHEN mf.type = 0 THEN mf.size END), 0) * 8.0 / 1024 AS float) AS data_mb,
                CAST(ISNULL(SUM(CASE WHEN mf.type = 1 THEN mf.size END), 0) * 8.0 / 1024 AS float) AS log_mb,
                d.compatibility_level,
                d.create_date,
                (
                    SELECT MAX(bus.backup_finish_date)
                    FROM msdb.dbo.backupset bus
                    WHERE bus.database_name = d.name AND bus.type = 'D'
                ) AS last_full,
                (
                    SELECT MAX(bus.backup_finish_date)
                    FROM msdb.dbo.backupset bus
                    WHERE bus.database_name = d.name AND bus.type = 'L'
                ) AS last_log
            FROM sys.databases d
            LEFT JOIN sys.master_files mf ON mf.database_id = d.database_id
            GROUP BY d.name, d.state_desc, d.recovery_model_desc, d.collation_name, d.compatibility_level, d.create_date
            ORDER BY d.name;
            """;

        await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var list = new List<DatabaseAdminInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            list.Add(new DatabaseAdminInfo
            {
                Name = name,
                State = reader.GetString(1),
                RecoveryModel = reader.GetString(2),
                Collation = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                DataSizeMb = reader.GetDouble(4),
                LogSizeMb = reader.GetDouble(5),
                CompatibilityLevel = reader.GetByte(6),
                CreateDate = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Local)),
                LastFullBackup = reader.IsDBNull(8) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Local)),
                LastLogBackup = reader.IsDBNull(9) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Local)),
                IsSystem = SystemDatabases.Contains(name)
            });
        }

        return list;
    }

    public async Task<AdminOperationResult> CreateDatabaseAsync(
        ConnectionDefinition connection,
        string? password,
        CreateDatabaseRequest request,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var messages = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return AdminOperationResult.Fail("Database name is required.");
        }

        if (!IsSafeIdentifier(request.Name))
        {
            return AdminOperationResult.Fail("Database name contains invalid characters.");
        }

        var recovery = (request.RecoveryModel ?? "SIMPLE").ToUpperInvariant() switch
        {
            "FULL" => "FULL",
            "BULK_LOGGED" => "BULK_LOGGED",
            _ => "SIMPLE"
        };

        var dataMb = Math.Clamp(request.InitialDataSizeMb, 1, 102400);
        var logMb = Math.Clamp(request.InitialLogSizeMb, 1, 102400);
        var collationClause = string.IsNullOrWhiteSpace(request.Collation)
            ? string.Empty
            : $" COLLATE {QuoteIdent(request.Collation!)}";

        var sql = $"""
            CREATE DATABASE {QuoteIdent(request.Name)}{collationClause};
            ALTER DATABASE {QuoteIdent(request.Name)} SET RECOVERY {recovery};
            ALTER DATABASE {QuoteIdent(request.Name)} MODIFY FILE (NAME = N'{EscapeLiteral(request.Name)}', SIZE = {dataMb}MB);
            ALTER DATABASE {QuoteIdent(request.Name)} MODIFY FILE (NAME = N'{EscapeLiteral(request.Name)}_log', SIZE = {logMb}MB);
            """;

        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            conn.InfoMessage += (_, e) => messages.Add(e.Message);

            // CREATE can briefly fail with exclusive lock on model under concurrent load — retry.
            Exception? lastCreateError = null;
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    await using var create = new SqlCommand($"CREATE DATABASE {QuoteIdent(request.Name)}{collationClause};", conn)
                    {
                        CommandTimeout = 120
                    };
                    await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    lastCreateError = null;
                    break;
                }
                catch (SqlException ex) when (ex.Number is 1807 or 5030 or 1205 || ex.Message.Contains("exclusive lock on database 'model'", StringComparison.OrdinalIgnoreCase))
                {
                    lastCreateError = ex;
                    messages.Add($"Create attempt {attempt}/5 waiting on model lock…");
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
                }
            }

            if (lastCreateError is not null)
            {
                throw lastCreateError;
            }

            await using (var alterRec = new SqlCommand($"ALTER DATABASE {QuoteIdent(request.Name)} SET RECOVERY {recovery};", conn))
            {
                await alterRec.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Best-effort size adjust using actual logical file names
            await TryResizeFilesAsync(conn, request.Name, dataMb, logMb, messages, cancellationToken).ConfigureAwait(false);

            sw.Stop();
            messages.Insert(0, $"Database [{request.Name}] created with recovery {recovery}.");
            return AdminOperationResult.Ok($"Created database {request.Name}.", sw.ElapsedMilliseconds, messages);
        }
        catch (Exception ex)
        {
            sw.Stop();
            messages.Add(ex.Message);
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds, messages);
        }
    }

    public async Task<AdminOperationResult> BackupDatabaseAsync(
        ConnectionDefinition connection,
        string? password,
        BackupDatabaseRequest request,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var messages = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Database) || string.IsNullOrWhiteSpace(request.BackupPath))
        {
            return AdminOperationResult.Fail("Database and backup path are required.");
        }

        if (!IsSafeIdentifier(request.Database))
        {
            return AdminOperationResult.Fail("Database name contains invalid characters.");
        }

        var options = new List<string>();
        if (request.Init)
        {
            options.Add("INIT");
        }

        if (request.CopyOnly)
        {
            options.Add("COPY_ONLY");
        }

        if (request.Compress)
        {
            options.Add("COMPRESSION");
        }

        if (request.Verify)
        {
            options.Add("CHECKSUM");
        }

        if (!string.IsNullOrWhiteSpace(request.Description))
        {
            options.Add($"DESCRIPTION = N'{EscapeLiteral(request.Description)}'");
        }

        options.Add("STATS = 10");
        var withClause = options.Count > 0 ? " WITH " + string.Join(", ", options) : string.Empty;

        var backupSql = $"""
            BACKUP DATABASE {QuoteIdent(request.Database)}
            TO DISK = N'{EscapeLiteral(request.BackupPath)}'{withClause};
            """;

        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            conn.InfoMessage += (_, e) => messages.Add(e.Message);

            await using (var cmd = new SqlCommand(backupSql, conn) { CommandTimeout = 0 })
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (request.Verify)
            {
                var verifySql = $"""
                    RESTORE VERIFYONLY
                    FROM DISK = N'{EscapeLiteral(request.BackupPath)}'
                    WITH CHECKSUM;
                    """;
                await using var verify = new SqlCommand(verifySql, conn) { CommandTimeout = 0 };
                await verify.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                messages.Add("Backup verified successfully (RESTORE VERIFYONLY).");
            }

            sw.Stop();
            messages.Insert(0, $"Backup of [{request.Database}] completed → {request.BackupPath}");
            return AdminOperationResult.Ok($"Backup completed for {request.Database}.", sw.ElapsedMilliseconds, messages);
        }
        catch (Exception ex)
        {
            sw.Stop();
            messages.Add(ex.Message);
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds, messages);
        }
    }

    public async Task<AdminOperationResult> RestoreDatabaseAsync(
        ConnectionDefinition connection,
        string? password,
        RestoreDatabaseRequest request,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var messages = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Database) || string.IsNullOrWhiteSpace(request.BackupPath))
        {
            return AdminOperationResult.Fail("Database and backup path are required.");
        }

        if (!IsSafeIdentifier(request.Database))
        {
            return AdminOperationResult.Fail("Database name contains invalid characters.");
        }

        var options = new List<string> { "STATS = 10" };
        if (request.Replace)
        {
            options.Add("REPLACE");
        }

        options.Add(request.Recover ? "RECOVERY" : "NORECOVERY");
        var withClause = " WITH " + string.Join(", ", options);

        var sql = $"""
            RESTORE DATABASE {QuoteIdent(request.Database)}
            FROM DISK = N'{EscapeLiteral(request.BackupPath)}'{withClause};
            """;

        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            conn.InfoMessage += (_, e) => messages.Add(e.Message);

            // Kick users if replacing an online database
            if (request.Replace)
            {
                var kick = $"""
                    IF DB_ID(N'{EscapeLiteral(request.Database)}') IS NOT NULL
                    BEGIN
                        ALTER DATABASE {QuoteIdent(request.Database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    END
                    """;
                await using var kickCmd = new SqlCommand(kick, conn) { CommandTimeout = 60 };
                await kickCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 })
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (request.Replace && request.Recover)
            {
                var multi = $"ALTER DATABASE {QuoteIdent(request.Database)} SET MULTI_USER;";
                await using var multiCmd = new SqlCommand(multi, conn);
                await multiCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            sw.Stop();
            messages.Insert(0, $"Restore of [{request.Database}] completed from {request.BackupPath}");
            return AdminOperationResult.Ok($"Restore completed for {request.Database}.", sw.ElapsedMilliseconds, messages);
        }
        catch (Exception ex)
        {
            sw.Stop();
            messages.Add(ex.Message);
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds, messages);
        }
    }

    public async Task<IReadOnlyList<BackupSetInfo>> GetRecentBackupsAsync(
        ConnectionDefinition connection,
        string? password,
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (100)
                ISNULL(bs.name, ''),
                CASE bs.type WHEN 'D' THEN 'Full' WHEN 'I' THEN 'Diff' WHEN 'L' THEN 'Log' ELSE bs.type END,
                bs.backup_start_date,
                CAST(bs.backup_size / 1024.0 / 1024.0 AS float),
                bmf.physical_device_name,
                ISNULL(bs.description, '')
            FROM msdb.dbo.backupset bs
            LEFT JOIN msdb.dbo.backupmediafamily bmf ON bmf.media_set_id = bs.media_set_id
            WHERE (@db IS NULL OR bs.database_name = @db)
            ORDER BY bs.backup_start_date DESC;
            """;

        await using var conn = SqlServerConnectionFactory.Create(connection, password, "msdb");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@db", (object?)database ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var list = new List<BackupSetInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new BackupSetInfo
            {
                Name = reader.GetString(0),
                Type = reader.GetString(1),
                BackupStartDate = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Local)),
                BackupSizeMb = reader.GetDouble(3),
                PhysicalDeviceName = reader.IsDBNull(4) ? null : reader.GetString(4),
                Description = reader.IsDBNull(5) ? null : reader.GetString(5)
            });
        }

        return list;
    }

    public async Task<string> SuggestBackupPathAsync(
        ConnectionDefinition connection,
        string? password,
        string database,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (1) physical_device_name
            FROM msdb.dbo.backupmediafamily
            WHERE physical_device_name LIKE '%.bak'
            ORDER BY media_set_id DESC;
            """;

        string? sample = null;
        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "msdb");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand(sql, conn);
            sample = (await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) as string;
        }
        catch
        {
            // fall through to defaults
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var fileName = $"{SanitizeFilePart(database)}_{stamp}.bak";

        if (!string.IsNullOrWhiteSpace(sample))
        {
            try
            {
                var dir = Path.GetDirectoryName(sample.Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    var sep = sample.Contains('\\') ? "\\" : "/";
                    return dir.Replace('\\', sep[0]).Replace('/', sep[0]).TrimEnd('\\', '/') + sep + fileName;
                }
            }
            catch
            {
                // ignore
            }
        }

        // Linux SQL Server default data directory; Windows instances usually accept this style too via default backup dir
        return $"/var/opt/mssql/data/{fileName}";
    }

    public async Task<AgentServiceStatus> GetAgentStatusAsync(
        ConnectionDefinition connection,
        string? password,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            // xp_servicecontrol works on Windows; on Linux check SQLAGENT via sys.dm_server_services when available
            const string dmSql = """
                SELECT TOP (1) status_desc, startup_type_desc
                FROM sys.dm_server_services
                WHERE servicename LIKE N'SQL Server Agent%'
                   OR servicename LIKE N'sqlagent%';
                """;

            await using (var dm = new SqlCommand(dmSql, conn))
            {
                await using var reader = await dm.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var status = reader.IsDBNull(0) ? "Unknown" : reader.GetString(0);
                    var startup = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    var running = status.Equals("Running", StringComparison.OrdinalIgnoreCase);
                    return new AgentServiceStatus
                    {
                        AgentAvailable = true,
                        AgentRunning = running,
                        StatusText = running ? "Running" : status,
                        Detail = string.IsNullOrWhiteSpace(startup) ? null : $"Startup: {startup}"
                    };
                }
            }

            // Fallback: can we read msdb jobs?
            await using var probe = new SqlCommand("SELECT TOP (1) job_id FROM msdb.dbo.sysjobs", conn);
            await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return new AgentServiceStatus
            {
                AgentAvailable = true,
                AgentRunning = false,
                StatusText = "Available (service status unknown)",
                Detail = "msdb job catalog is readable. Enable SQL Server Agent if jobs do not start."
            };
        }
        catch (Exception ex)
        {
            return new AgentServiceStatus
            {
                AgentAvailable = false,
                AgentRunning = false,
                StatusText = "Unavailable",
                Detail = ex.Message
            };
        }
    }

    public async Task<IReadOnlyList<AgentJobInfo>> GetAgentJobsAsync(
        ConnectionDefinition connection,
        string? password,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                j.job_id,
                j.name,
                j.enabled,
                ISNULL(c.name, N'[Uncategorized]'),
                SUSER_SNAME(j.owner_sid),
                CASE
                    WHEN ja.job_id IS NOT NULL THEN N'Executing'
                    ELSE N'Idle'
                END AS current_status,
                CASE ISNULL(h.run_status, -1)
                    WHEN 0 THEN N'Failed'
                    WHEN 1 THEN N'Succeeded'
                    WHEN 2 THEN N'Retry'
                    WHEN 3 THEN N'Canceled'
                    WHEN 4 THEN N'In progress'
                    ELSE N'Never run'
                END AS last_outcome,
                CASE
                    WHEN h.run_date IS NULL OR h.run_date = 0 THEN NULL
                    ELSE CONVERT(datetime,
                        CONVERT(char(8), h.run_date) + ' ' +
                        STUFF(STUFF(RIGHT('000000' + CONVERT(varchar(6), h.run_time), 6), 5, 0, ':'), 3, 0, ':'))
                END AS last_run,
                CASE
                    WHEN js.next_run_date IS NULL OR js.next_run_date = 0 THEN NULL
                    ELSE CONVERT(datetime,
                        CONVERT(char(8), js.next_run_date) + ' ' +
                        STUFF(STUFF(RIGHT('000000' + CONVERT(varchar(6), ISNULL(js.next_run_time, 0)), 6), 5, 0, ':'), 3, 0, ':'))
                END AS next_run,
                ISNULL(j.description, N''),
                (SELECT COUNT(*) FROM msdb.dbo.sysjobsteps st WHERE st.job_id = j.job_id)
            FROM msdb.dbo.sysjobs j
            LEFT JOIN msdb.dbo.syscategories c ON c.category_id = j.category_id
            LEFT JOIN msdb.dbo.sysjobschedules js ON js.job_id = j.job_id
            LEFT JOIN msdb.dbo.sysschedules s ON s.schedule_id = js.schedule_id
            OUTER APPLY (
                SELECT TOP (1) run_status, run_date, run_time
                FROM msdb.dbo.sysjobhistory h
                WHERE h.job_id = j.job_id AND h.step_id = 0
                ORDER BY h.instance_id DESC
            ) h
            LEFT JOIN msdb.dbo.sysjobactivity ja
                ON ja.job_id = j.job_id
               AND ja.start_execution_date IS NOT NULL
               AND ja.stop_execution_date IS NULL
               AND ja.session_id = (SELECT MAX(session_id) FROM msdb.dbo.syssessions)
            ORDER BY j.name;
            """;

        await using var conn = SqlServerConnectionFactory.Create(connection, password, "msdb");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var list = new List<AgentJobInfo>();
        var seen = new HashSet<Guid>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetGuid(0);
            if (!seen.Add(id))
            {
                continue; // multiple schedules can duplicate rows
            }

            list.Add(new AgentJobInfo
            {
                JobId = id,
                Name = reader.GetString(1),
                Enabled = reader.GetByte(2) == 1,
                Category = reader.GetString(3),
                Owner = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                CurrentStatus = reader.GetString(5),
                LastRunOutcome = reader.GetString(6),
                LastRun = reader.IsDBNull(7) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Local)),
                NextRun = reader.IsDBNull(8) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Local)),
                Description = reader.GetString(9),
                StepCount = reader.GetInt32(10)
            });
        }

        return list;
    }

    public async Task<IReadOnlyList<AgentJobStepInfo>> GetAgentJobStepsAsync(
        ConnectionDefinition connection,
        string? password,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                step_id,
                step_name,
                subsystem,
                ISNULL(command, N''),
                ISNULL(database_name, N''),
                CASE on_success_action
                    WHEN 1 THEN N'Quit with success'
                    WHEN 2 THEN N'Quit with failure'
                    WHEN 3 THEN N'Go to next step'
                    WHEN 4 THEN N'Go to step'
                    ELSE CONVERT(nvarchar(20), on_success_action)
                END,
                CASE on_fail_action
                    WHEN 1 THEN N'Quit with success'
                    WHEN 2 THEN N'Quit with failure'
                    WHEN 3 THEN N'Go to next step'
                    WHEN 4 THEN N'Go to step'
                    ELSE CONVERT(nvarchar(20), on_fail_action)
                END
            FROM msdb.dbo.sysjobsteps
            WHERE job_id = @jobId
            ORDER BY step_id;
            """;

        await using var conn = SqlServerConnectionFactory.Create(connection, password, "msdb");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@jobId", jobId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var list = new List<AgentJobStepInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new AgentJobStepInfo
            {
                StepId = reader.GetInt32(0),
                StepName = reader.GetString(1),
                Subsystem = reader.GetString(2),
                Command = reader.GetString(3),
                DatabaseName = reader.GetString(4),
                OnSuccessAction = reader.GetString(5),
                OnFailAction = reader.GetString(6)
            });
        }

        return list;
    }

    public async Task<IReadOnlyList<AgentJobHistoryEntry>> GetAgentJobHistoryAsync(
        ConnectionDefinition connection,
        string? password,
        Guid jobId,
        int maxRows = 50,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (@maxRows)
                instance_id,
                ISNULL(step_name, N'(Job outcome)'),
                step_id,
                CASE run_status
                    WHEN 0 THEN N'Failed'
                    WHEN 1 THEN N'Succeeded'
                    WHEN 2 THEN N'Retry'
                    WHEN 3 THEN N'Canceled'
                    WHEN 4 THEN N'In progress'
                    ELSE N'Unknown'
                END,
                CONVERT(datetime,
                    CONVERT(char(8), run_date) + ' ' +
                    STUFF(STUFF(RIGHT('000000' + CONVERT(varchar(6), run_time), 6), 5, 0, ':'), 3, 0, ':')),
                STUFF(STUFF(RIGHT('000000' + CONVERT(varchar(6), run_duration), 6), 5, 0, ':'), 3, 0, ':'),
                ISNULL(message, N'')
            FROM msdb.dbo.sysjobhistory
            WHERE job_id = @jobId
            ORDER BY instance_id DESC;
            """;

        await using var conn = SqlServerConnectionFactory.Create(connection, password, "msdb");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@jobId", jobId);
        cmd.Parameters.AddWithValue("@maxRows", Math.Clamp(maxRows, 1, 500));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var list = new List<AgentJobHistoryEntry>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new AgentJobHistoryEntry
            {
                InstanceId = reader.GetInt32(0),
                StepName = reader.GetString(1),
                StepId = reader.GetInt32(2),
                RunStatus = reader.GetString(3),
                RunDate = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Local)),
                RunDuration = reader.GetString(5),
                Message = reader.GetString(6)
            });
        }

        return list;
    }

    public async Task<AdminOperationResult> StartAgentJobAsync(
        ConnectionDefinition connection,
        string? password,
        string jobName,
        CancellationToken cancellationToken = default)
        => await ExecAgentProcAsync(connection, password, "msdb.dbo.sp_start_job", jobName, "started", cancellationToken)
            .ConfigureAwait(false);

    public async Task<AdminOperationResult> StopAgentJobAsync(
        ConnectionDefinition connection,
        string? password,
        string jobName,
        CancellationToken cancellationToken = default)
        => await ExecAgentProcAsync(connection, password, "msdb.dbo.sp_stop_job", jobName, "stop requested", cancellationToken)
            .ConfigureAwait(false);

    public async Task<AdminOperationResult> SetAgentJobEnabledAsync(
        ConnectionDefinition connection,
        string? password,
        string jobName,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "msdb");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand("msdb.dbo.sp_update_job", conn)
            {
                CommandType = System.Data.CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@job_name", jobName);
            cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();
            var verb = enabled ? "enabled" : "disabled";
            return AdminOperationResult.Ok($"Job '{jobName}' {verb}.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public async Task<ServerAnalysisSnapshot> GetServerAnalysisAsync(
        ConnectionDefinition connection,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var databases = await GetDatabaseAdminInfoAsync(connection, password, cancellationToken).ConfigureAwait(false);
        var agent = await GetAgentStatusAsync(connection, password, cancellationToken).ConfigureAwait(false);

        await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        string serverName = conn.DataSource;
        string version = string.Empty;
        string edition = string.Empty;
        string productLevel = string.Empty;
        int cpu = 0;
        double mem = 0;
        DateTimeOffset start = DateTimeOffset.Now;

        await using (var info = new SqlCommand("""
            SELECT
                @@SERVERNAME,
                CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)),
                CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
                CAST(SERVERPROPERTY('ProductLevel') AS nvarchar(128)),
                (SELECT cpu_count FROM sys.dm_os_sys_info),
                (SELECT physical_memory_kb / 1024.0 FROM sys.dm_os_sys_info),
                (SELECT sqlserver_start_time FROM sys.dm_os_sys_info);
            """, conn))
        {
            await using var reader = await info.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                serverName = reader.IsDBNull(0) ? serverName : reader.GetString(0);
                version = reader.IsDBNull(1) ? "" : reader.GetString(1);
                edition = reader.IsDBNull(2) ? "" : reader.GetString(2);
                productLevel = reader.IsDBNull(3) ? "" : reader.GetString(3);
                cpu = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4));
                mem = reader.IsDBNull(5) ? 0 : Convert.ToDouble(reader.GetValue(5));
                start = reader.IsDBNull(6)
                    ? DateTimeOffset.Now
                    : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Local));
            }
        }

        var sessions = await QuerySessionsAsync(conn, cancellationToken).ConfigureAwait(false);
        var waits = await QueryWaitsAsync(conn, cancellationToken).ConfigureAwait(false);

        return new ServerAnalysisSnapshot
        {
            ServerName = serverName,
            Version = version,
            Edition = edition,
            ProductLevel = productLevel,
            CpuCount = cpu,
            PhysicalMemoryMb = mem,
            ServerStartTime = start,
            UserDatabaseCount = databases.Count(d => !d.IsSystem),
            TotalDataSizeMb = databases.Sum(d => d.DataSizeMb),
            TotalLogSizeMb = databases.Sum(d => d.LogSizeMb),
            Agent = agent,
            Databases = databases,
            TopSessions = sessions,
            TopWaits = waits
        };
    }

    private static async Task<IReadOnlyList<SessionActivityInfo>> QuerySessionsAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT TOP (25)
                r.session_id,
                ISNULL(s.login_name, N''),
                ISNULL(s.host_name, N''),
                ISNULL(DB_NAME(r.database_id), N''),
                ISNULL(r.status, N''),
                ISNULL(r.command, N''),
                ISNULL(r.wait_type, N''),
                r.cpu_time,
                r.total_elapsed_time,
                SUBSTRING(t.text, (r.statement_start_offset / 2) + 1,
                    ((CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(t.text) ELSE r.statement_end_offset END
                      - r.statement_start_offset) / 2) + 1)
            FROM sys.dm_exec_requests r
            INNER JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id
            OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
            WHERE r.session_id <> @@SPID
              AND s.is_user_process = 1
            ORDER BY r.total_elapsed_time DESC;
            """;

        try
        {
            await using var cmd = new SqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var list = new List<SessionActivityInfo>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(new SessionActivityInfo
                {
                    SessionId = reader.GetInt32(0),
                    LoginName = reader.GetString(1),
                    HostName = reader.GetString(2),
                    Database = reader.GetString(3),
                    Status = reader.GetString(4),
                    Command = reader.GetString(5),
                    WaitType = reader.GetString(6),
                    CpuTimeMs = reader.GetInt32(7),
                    ElapsedTimeMs = reader.GetInt32(8),
                    SqlText = reader.IsDBNull(9) ? null : reader.GetString(9)
                });
            }

            return list;
        }
        catch
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<WaitStatInfo>> QueryWaitsAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT TOP (15)
                wait_type,
                waiting_tasks_count,
                wait_time_ms / 1000.0,
                signal_wait_time_ms / 1000.0
            FROM sys.dm_os_wait_stats
            WHERE wait_type NOT LIKE 'SLEEP%'
              AND wait_type NOT LIKE 'BROKER%'
              AND wait_type NOT LIKE 'XE%'
              AND wait_type NOT IN (
                'CLR_AUTO_EVENT','CLR_MANUAL_EVENT','LAZYWRITER_SLEEP','DIRTY_PAGE_POLL',
                'HADR_FILESTREAM_IOMGR_IOCOMPLETION','SQLTRACE_BUFFER_FLUSH','WAITFOR',
                'CHECKPOINT_QUEUE','REQUEST_FOR_DEADLOCK_SEARCH','XE_TIMER_EVENT',
                'LOGMGR_QUEUE','FT_IFTS_SCHEDULER_IDLE_WAIT','BROKER_TO_FLUSH',
                'SP_SERVER_DIAGNOSTICS_SLEEP','QDS_PERSIST_TASK_MAIN_LOOP_SLEEP',
                'QDS_ASYNC_QUEUE','WAIT_XTP_OFFLINE_CKPT_NEW_LOG','SOS_WORK_DISPATCHER')
            ORDER BY wait_time_ms DESC;
            """;

        try
        {
            await using var cmd = new SqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var list = new List<WaitStatInfo>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(new WaitStatInfo
                {
                    WaitType = reader.GetString(0),
                    WaitingTasksCount = Convert.ToInt64(reader.GetValue(1)),
                    WaitTimeSec = Convert.ToDouble(reader.GetValue(2)),
                    SignalWaitTimeSec = Convert.ToDouble(reader.GetValue(3))
                });
            }

            return list;
        }
        catch
        {
            return [];
        }
    }

    private static async Task<AdminOperationResult> ExecAgentProcAsync(
        ConnectionDefinition connection,
        string? password,
        string procName,
        string jobName,
        string verb,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var messages = new List<string>();
        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "msdb");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            conn.InfoMessage += (_, e) => messages.Add(e.Message);
            await using var cmd = new SqlCommand(procName, conn)
            {
                CommandType = System.Data.CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@job_name", jobName);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();
            messages.Insert(0, $"Job '{jobName}' {verb}.");
            return AdminOperationResult.Ok($"Job '{jobName}' {verb}.", sw.ElapsedMilliseconds, messages);
        }
        catch (Exception ex)
        {
            sw.Stop();
            messages.Add(ex.Message);
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds, messages);
        }
    }

    private static async Task TryResizeFilesAsync(
        SqlConnection conn,
        string database,
        int dataMb,
        int logMb,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        try
        {
            var sql = """
                SELECT mf.name, mf.type
                FROM sys.master_files mf
                INNER JOIN sys.databases d ON d.database_id = mf.database_id
                WHERE d.name = @db;
                """;
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@db", database);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var files = new List<(string Name, int Type)>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                files.Add((reader.GetString(0), Convert.ToInt32(reader.GetValue(1))));
            }

            await reader.DisposeAsync().ConfigureAwait(false);

            foreach (var (name, type) in files)
            {
                var size = type == 0 ? dataMb : logMb;
                var alter = $"ALTER DATABASE {QuoteIdent(database)} MODIFY FILE (NAME = N'{EscapeLiteral(name)}', SIZE = {size}MB);";
                await using var alterCmd = new SqlCommand(alter, conn);
                await alterCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            messages.Add($"Database created; optional size adjust skipped: {ex.Message}");
        }
    }

    private static bool IsSafeIdentifier(string name)
        => name.Length is > 0 and <= 128
           && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or ' ');

    private static string QuoteIdent(string name) => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string EscapeLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string SanitizeFilePart(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        return sb.ToString();
    }
}
