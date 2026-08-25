using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using PSMS.Core.Abstractions;
using PSMS.Core.Models;
using PSMS.Core.Models.Admin;

namespace PSMS.Providers.SqlServer;

public sealed class SqlServerExtendedEventsService : IExtendedEventsService
{
    private static readonly XEventTemplate[] Templates =
    [
        new() { Id = "tsql_duration", Name = "TSQL + Duration", Description = "rpc_completed and sql_batch_completed with duration." },
        new() { Id = "errors", Name = "Errors", Description = "error_reported events." },
        new() { Id = "locks", Name = "Locks", Description = "lock_acquired / lock_released (noisy)." },
        new() { Id = "standard", Name = "Standard", Description = "Batches, RPCs, and attention events." }
    ];

    public IReadOnlyList<XEventTemplate> GetTemplates() => Templates;

    public async Task<IReadOnlyList<XEventSessionInfo>> GetSessionsAsync(
        ConnectionDefinition connection,
        string? password,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT s.name, CASE WHEN d.name IS NULL THEN 0 ELSE 1 END, ISNULL(t.target_name, N''), s.create_time
            FROM sys.server_event_sessions s
            LEFT JOIN sys.dm_xe_sessions d ON d.name = s.name
            LEFT JOIN sys.server_event_session_targets t ON t.event_session_id = s.event_session_id
            ORDER BY s.name;
            """;
        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var list = new List<XEventSessionInfo>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(new XEventSessionInfo
                {
                    Name = reader.GetString(0),
                    IsRunning = Convert.ToInt32(reader.GetValue(1)) == 1,
                    Target = reader.GetString(2),
                    CreateTime = reader.IsDBNull(3)
                        ? null
                        : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Local))
                });
            }

            return list;
        }
        catch
        {
            return [];
        }
    }

    public async Task<AdminOperationResult> StartSessionAsync(
        ConnectionDefinition connection,
        string? password,
        StartProfilerSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var name = SanitizeSessionName(request.SessionName);
        try
        {
            var ddl = BuildCreateSessionDdl(name, request);
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Drop existing with same name
            await TryDropAsync(conn, name, cancellationToken).ConfigureAwait(false);

            await using (var create = new SqlCommand(ddl, conn) { CommandTimeout = 60 })
            {
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var start = new SqlCommand($"ALTER EVENT SESSION [{name}] ON SERVER STATE = START;", conn))
            {
                await start.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            sw.Stop();
            return AdminOperationResult.Ok($"Profiler session '{name}' started.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public async Task<AdminOperationResult> StopSessionAsync(
        ConnectionDefinition connection,
        string? password,
        string sessionName,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var name = SanitizeSessionName(sessionName);
        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand($"ALTER EVENT SESSION [{name}] ON SERVER STATE = STOP;", conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return AdminOperationResult.Ok($"Session '{name}' stopped.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public async Task<AdminOperationResult> DropSessionAsync(
        ConnectionDefinition connection,
        string? password,
        string sessionName,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var name = SanitizeSessionName(sessionName);
        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await TryDropAsync(conn, name, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return AdminOperationResult.Ok($"Session '{name}' dropped.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public async Task<IReadOnlyList<ProfilerEvent>> ReadEventsAsync(
        ConnectionDefinition connection,
        string? password,
        string sessionName,
        CancellationToken cancellationToken = default)
    {
        var name = SanitizeSessionName(sessionName);
        const string sql = """
            SELECT CAST(target_data AS xml)
            FROM sys.dm_xe_session_targets t
            INNER JOIN sys.dm_xe_sessions s ON s.address = t.event_session_address
            WHERE s.name = @name AND t.target_name = N'ring_buffer';
            """;
        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "master");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@name", name);
            var xml = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (string.IsNullOrWhiteSpace(xml))
            {
                return [];
            }

            return ParseRingBuffer(xml);
        }
        catch
        {
            return [];
        }
    }

    public Task<string> ScriptSessionAsync(
        ConnectionDefinition connection,
        string? password,
        StartProfilerSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        var name = SanitizeSessionName(request.SessionName);
        var ddl = BuildCreateSessionDdl(name, request);
        var script = ddl + Environment.NewLine + $"ALTER EVENT SESSION [{name}] ON SERVER STATE = START;";
        return Task.FromResult(script);
    }

    private static async Task TryDropAsync(SqlConnection conn, string name, CancellationToken ct)
    {
        try
        {
            await using var stop = new SqlCommand($"IF EXISTS (SELECT 1 FROM sys.dm_xe_sessions WHERE name = N'{Escape(name)}') ALTER EVENT SESSION [{name}] ON SERVER STATE = STOP;", conn);
            await stop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        try
        {
            await using var drop = new SqlCommand($"IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = N'{Escape(name)}') DROP EVENT SESSION [{name}] ON SERVER;", conn);
            await drop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    private static string BuildCreateSessionDdl(string name, StartProfilerSessionRequest request)
    {
        var mem = Math.Clamp(request.MaxMemoryKb, 1024, 102400);
        var events = request.TemplateId switch
        {
            "errors" => """
                ADD EVENT sqlserver.error_reported(
                    ACTION(sqlserver.client_app_name, sqlserver.database_name, sqlserver.session_id, sqlserver.sql_text, sqlserver.username))
                """,
            "locks" => """
                ADD EVENT sqlserver.lock_acquired(
                    ACTION(sqlserver.client_app_name, sqlserver.database_name, sqlserver.session_id, sqlserver.sql_text, sqlserver.username)),
                ADD EVENT sqlserver.lock_released(
                    ACTION(sqlserver.client_app_name, sqlserver.database_name, sqlserver.session_id, sqlserver.sql_text, sqlserver.username))
                """,
            "standard" => """
                ADD EVENT sqlserver.sql_batch_completed(
                    ACTION(sqlserver.client_app_name, sqlserver.database_name, sqlserver.session_id, sqlserver.sql_text, sqlserver.username)),
                ADD EVENT sqlserver.rpc_completed(
                    ACTION(sqlserver.client_app_name, sqlserver.database_name, sqlserver.session_id, sqlserver.sql_text, sqlserver.username)),
                ADD EVENT sqlserver.attention(
                    ACTION(sqlserver.client_app_name, sqlserver.database_name, sqlserver.session_id, sqlserver.username))
                """,
            _ => """
                ADD EVENT sqlserver.sql_batch_completed(
                    ACTION(sqlserver.client_app_name, sqlserver.database_name, sqlserver.session_id, sqlserver.sql_text, sqlserver.username)),
                ADD EVENT sqlserver.rpc_completed(
                    ACTION(sqlserver.client_app_name, sqlserver.database_name, sqlserver.session_id, sqlserver.sql_text, sqlserver.username))
                """
        };

        var filterParts = new List<string>();
        if (request.ExcludeSystem)
        {
            filterParts.Add("[sqlserver].[is_system]=(0)");
        }

        if (request.MinDurationMs > 0)
        {
            filterParts.Add($"[duration]>=({request.MinDurationMs * 1000L})");
        }

        if (!string.IsNullOrWhiteSpace(request.DatabaseName))
        {
            filterParts.Add($"[sqlserver].[database_name]=N'{Escape(request.DatabaseName)}'");
        }

        if (!string.IsNullOrWhiteSpace(request.LoginName))
        {
            filterParts.Add($"[sqlserver].[username]=N'{Escape(request.LoginName)}'");
        }

        if (!string.IsNullOrWhiteSpace(request.ApplicationName))
        {
            filterParts.Add($"[sqlserver].[client_app_name]=N'{Escape(request.ApplicationName)}'");
        }

        var where = filterParts.Count > 0
            ? ", SET collect_system_database_partition = 0 WHERE " + string.Join(" AND ", filterParts)
            : string.Empty;

        // Prefer embedding WHERE after ACTION lists for TSQL templates.
        if (filterParts.Count > 0 && request.TemplateId is not ("errors" or "locks"))
        {
            events = events.Replace(
                "ACTION(sqlserver.client_app_name, sqlserver.database_name, sqlserver.session_id, sqlserver.sql_text, sqlserver.username))",
                "ACTION(sqlserver.client_app_name, sqlserver.database_name, sqlserver.session_id, sqlserver.sql_text, sqlserver.username) WHERE " + string.Join(" AND ", filterParts) + ")",
                StringComparison.Ordinal);
            where = string.Empty;
        }

        return $"""
            CREATE EVENT SESSION [{name}] ON SERVER
            {events}
            ADD TARGET package0.ring_buffer(SET max_memory = {mem})
            WITH (MAX_MEMORY = {mem} KB, EVENT_RETENTION_MODE = ALLOW_SINGLE_EVENT_LOSS, MAX_DISPATCH_LATENCY = 1 SECONDS, TRACK_CAUSALITY = OFF, STARTUP_STATE = OFF);
            """;
    }

    private static IReadOnlyList<ProfilerEvent> ParseRingBuffer(string xml)
    {
        var list = new List<ProfilerEvent>();
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var ev in doc.Descendants("event").Take(500))
            {
                var name = (string?)ev.Attribute("name") ?? "event";
                var tsAttr = (string?)ev.Attribute("timestamp");
                DateTimeOffset ts = DateTimeOffset.Now;
                if (DateTimeOffset.TryParse(tsAttr, out var parsed))
                {
                    ts = parsed;
                }

                string GetData(string n) =>
                    ev.Elements("data").FirstOrDefault(d => (string?)d.Attribute("name") == n)?.Element("value")?.Value
                    ?? ev.Elements("action").FirstOrDefault(d => (string?)d.Attribute("name") == n)?.Element("value")?.Value
                    ?? string.Empty;

                long.TryParse(GetData("duration"), out var durationUs);
                long.TryParse(GetData("cpu_time"), out var cpuUs);
                long.TryParse(GetData("logical_reads"), out var reads);
                long.TryParse(GetData("writes"), out var writes);
                int.TryParse(GetData("session_id"), out var spid);

                list.Add(new ProfilerEvent
                {
                    Timestamp = ts,
                    EventName = name,
                    DatabaseName = GetData("database_name"),
                    LoginName = GetData("username"),
                    ClientApp = GetData("client_app_name"),
                    DurationMs = durationUs / 1000,
                    CpuMs = cpuUs / 1000,
                    Reads = reads,
                    Writes = writes,
                    SessionId = spid,
                    SqlText = GetData("statement").Length > 0 ? GetData("statement") : GetData("batch_text").Length > 0 ? GetData("batch_text") : GetData("sql_text")
                });
            }
        }
        catch
        {
            // ignore parse errors
        }

        return list.OrderByDescending(e => e.Timestamp).ToList();
    }

    private static string SanitizeSessionName(string? name)
    {
        var raw = string.IsNullOrWhiteSpace(name) ? "psms_profiler" : name.Trim();
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '_' ? c : '_');
        }

        var s = sb.ToString();
        return s.Length == 0 ? "psms_profiler" : s[..Math.Min(s.Length, 64)];
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
