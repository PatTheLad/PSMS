using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using PSMS.Core.Abstractions;
using PSMS.Core.Models;
using PSMS.Core.Models.Admin;

namespace PSMS.Providers.SqlServer;

public sealed partial class SqlServerAdminService
{
    public async Task<IReadOnlyList<AgentJobScheduleInfo>> GetAgentJobSchedulesAsync(
        ConnectionDefinition connection,
        string? password,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                s.schedule_id,
                s.name,
                s.enabled,
                CASE s.freq_type
                    WHEN 1 THEN N'Once'
                    WHEN 4 THEN N'Daily'
                    WHEN 8 THEN N'Weekly'
                    WHEN 16 THEN N'Monthly'
                    ELSE N'Custom'
                END,
                CASE
                    WHEN js.next_run_date IS NULL OR js.next_run_date = 0 THEN NULL
                    ELSE DATETIMEFROMPARTS(
                        js.next_run_date / 10000,
                        (js.next_run_date % 10000) / 100,
                        js.next_run_date % 100,
                        js.next_run_time / 10000,
                        (js.next_run_time % 10000) / 100,
                        js.next_run_time % 100,
                        0)
                END
            FROM msdb.dbo.sysjobschedules js
            INNER JOIN msdb.dbo.sysschedules s ON s.schedule_id = js.schedule_id
            WHERE js.job_id = @job_id
            ORDER BY s.name;
            """;

        await using var conn = SqlServerConnectionFactory.Create(connection, password, "msdb");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@job_id", jobId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<AgentJobScheduleInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new AgentJobScheduleInfo
            {
                ScheduleId = reader.GetInt32(0),
                Name = reader.GetString(1),
                Enabled = reader.GetByte(2) == 1,
                FrequencyDescription = reader.GetString(3),
                NextRun = reader.IsDBNull(4)
                    ? null
                    : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Local))
            });
        }

        return list;
    }

    public async Task<AdminOperationResult> CreateAgentJobAsync(
        ConnectionDefinition connection,
        string? password,
        CreateAgentJobRequest request,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var messages = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return AdminOperationResult.Fail("Job name is required.");
        }

        if (request.Steps.Count == 0)
        {
            return AdminOperationResult.Fail("At least one job step is required.");
        }

        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "msdb");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            conn.InfoMessage += (_, e) => messages.Add(e.Message);

            await using (var addJob = new SqlCommand("msdb.dbo.sp_add_job", conn) { CommandType = System.Data.CommandType.StoredProcedure })
            {
                addJob.Parameters.AddWithValue("@job_name", request.Name);
                addJob.Parameters.AddWithValue("@enabled", request.Enabled ? 1 : 0);
                addJob.Parameters.AddWithValue("@description", (object?)request.Description ?? DBNull.Value);
                addJob.Parameters.AddWithValue("@category_name", string.IsNullOrWhiteSpace(request.Category) ? "[Uncategorized]" : request.Category);
                if (!string.IsNullOrWhiteSpace(request.OwnerLoginName))
                {
                    addJob.Parameters.AddWithValue("@owner_login_name", request.OwnerLoginName);
                }

                if (!string.IsNullOrWhiteSpace(request.NotifyOperatorOnFailure))
                {
                    addJob.Parameters.AddWithValue("@notify_level_email", 2); // on failure
                    addJob.Parameters.AddWithValue("@notify_email_operator_name", request.NotifyOperatorOnFailure);
                }

                await addJob.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var stepId = 1;
            foreach (var step in request.Steps)
            {
                await AddJobStepAsync(conn, request.Name, step, stepId++, cancellationToken).ConfigureAwait(false);
            }

            if (request.Schedule is not null)
            {
                await AttachScheduleAsync(conn, request.Name, request.Schedule, cancellationToken).ConfigureAwait(false);
            }

            await using (var server = new SqlCommand("msdb.dbo.sp_add_jobserver", conn) { CommandType = System.Data.CommandType.StoredProcedure })
            {
                server.Parameters.AddWithValue("@job_name", request.Name);
                server.Parameters.AddWithValue("@server_name", "(LOCAL)");
                await server.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            sw.Stop();
            messages.Insert(0, $"Job '{request.Name}' created with {request.Steps.Count} step(s).");
            return AdminOperationResult.Ok(messages[0], sw.ElapsedMilliseconds, messages);
        }
        catch (Exception ex)
        {
            sw.Stop();
            try
            {
                await DeleteAgentJobAsync(connection, password, request.Name, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // best-effort cleanup
            }

            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public async Task<AdminOperationResult> UpdateAgentJobAsync(
        ConnectionDefinition connection,
        string? password,
        UpdateAgentJobRequest request,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var messages = new List<string>();
        var jobName = request.Name;

        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "msdb");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            conn.InfoMessage += (_, e) => messages.Add(e.Message);

            await using (var upd = new SqlCommand("msdb.dbo.sp_update_job", conn) { CommandType = System.Data.CommandType.StoredProcedure })
            {
                upd.Parameters.AddWithValue("@job_name", jobName);
                upd.Parameters.AddWithValue("@enabled", request.Enabled ? 1 : 0);
                upd.Parameters.AddWithValue("@description", (object?)request.Description ?? DBNull.Value);
                upd.Parameters.AddWithValue("@category_name", string.IsNullOrWhiteSpace(request.Category) ? "[Uncategorized]" : request.Category);
                if (!string.IsNullOrWhiteSpace(request.NewName) &&
                    !string.Equals(request.NewName, jobName, StringComparison.OrdinalIgnoreCase))
                {
                    upd.Parameters.AddWithValue("@new_name", request.NewName);
                    jobName = request.NewName;
                }

                if (!string.IsNullOrWhiteSpace(request.NotifyOperatorOnFailure))
                {
                    upd.Parameters.AddWithValue("@notify_level_email", 2);
                    upd.Parameters.AddWithValue("@notify_email_operator_name", request.NotifyOperatorOnFailure);
                }

                await upd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Replace steps: delete existing then re-add
            var existing = await GetAgentJobStepsAsync(connection, password, request.JobId, cancellationToken).ConfigureAwait(false);
            foreach (var step in existing.OrderByDescending(s => s.StepId))
            {
                await using var del = new SqlCommand("msdb.dbo.sp_delete_jobstep", conn) { CommandType = System.Data.CommandType.StoredProcedure };
                del.Parameters.AddWithValue("@job_name", jobName);
                del.Parameters.AddWithValue("@step_id", step.StepId);
                await del.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var stepId = 1;
            foreach (var step in request.Steps)
            {
                await AddJobStepAsync(conn, jobName, step, stepId++, cancellationToken).ConfigureAwait(false);
            }

            if (request.Schedule is not null)
            {
                await AttachScheduleAsync(conn, jobName, request.Schedule, cancellationToken).ConfigureAwait(false);
            }

            sw.Stop();
            messages.Insert(0, $"Job '{jobName}' updated.");
            return AdminOperationResult.Ok(messages[0], sw.ElapsedMilliseconds, messages);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public async Task<AdminOperationResult> DeleteAgentJobAsync(
        ConnectionDefinition connection,
        string? password,
        string jobName,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "msdb");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand("msdb.dbo.sp_delete_job", conn) { CommandType = System.Data.CommandType.StoredProcedure };
            cmd.Parameters.AddWithValue("@job_name", jobName);
            cmd.Parameters.AddWithValue("@delete_unused_schedule", 1);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return AdminOperationResult.Ok($"Job '{jobName}' deleted.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public async Task<string> ScriptAgentJobAsync(
        ConnectionDefinition connection,
        string? password,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var jobs = await GetAgentJobsAsync(connection, password, cancellationToken).ConfigureAwait(false);
        var job = jobs.FirstOrDefault(j => j.JobId == jobId)
                  ?? throw new InvalidOperationException("Job not found.");
        var steps = await GetAgentJobStepsAsync(connection, password, jobId, cancellationToken).ConfigureAwait(false);
        var schedules = await GetAgentJobSchedulesAsync(connection, password, jobId, cancellationToken).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine("USE [msdb];");
        sb.AppendLine("GO");
        sb.AppendLine($"EXEC msdb.dbo.sp_add_job @job_name = N'{EscapeLiteral(job.Name)}',");
        sb.AppendLine($"    @enabled = {(job.Enabled ? 1 : 0)},");
        sb.AppendLine($"    @description = N'{EscapeLiteral(job.Description)}',");
        sb.AppendLine($"    @category_name = N'{EscapeLiteral(job.Category)}';");
        sb.AppendLine("GO");

        foreach (var step in steps)
        {
            sb.AppendLine($"EXEC msdb.dbo.sp_add_jobstep @job_name = N'{EscapeLiteral(job.Name)}',");
            sb.AppendLine($"    @step_name = N'{EscapeLiteral(step.StepName)}',");
            sb.AppendLine($"    @subsystem = N'{EscapeLiteral(step.Subsystem)}',");
            sb.AppendLine($"    @command = N'{EscapeLiteral(step.Command)}',");
            sb.AppendLine($"    @database_name = N'{EscapeLiteral(step.DatabaseName)}';");
            sb.AppendLine("GO");
        }

        foreach (var sched in schedules)
        {
            sb.AppendLine($"-- Schedule: {sched.Name} ({sched.FrequencyDescription})");
        }

        sb.AppendLine($"EXEC msdb.dbo.sp_add_jobserver @job_name = N'{EscapeLiteral(job.Name)}', @server_name = N'(LOCAL)';");
        sb.AppendLine("GO");
        return sb.ToString();
    }

    public async Task<IReadOnlyList<AgentOperatorInfo>> GetAgentOperatorsAsync(
        ConnectionDefinition connection,
        string? password,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, name, enabled, ISNULL(email_address, N''), ISNULL(pager_address, N'')
            FROM msdb.dbo.sysoperators
            ORDER BY name;
            """;
        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "msdb");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var list = new List<AgentOperatorInfo>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(new AgentOperatorInfo
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Enabled = reader.GetByte(2) == 1,
                    EmailAddress = reader.GetString(3),
                    PagerAddress = reader.GetString(4)
                });
            }

            return list;
        }
        catch
        {
            return [];
        }
    }

    public async Task<AdminOperationResult> CreateAgentOperatorAsync(
        ConnectionDefinition connection,
        string? password,
        CreateAgentOperatorRequest request,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return AdminOperationResult.Fail("Operator name is required.");
        }

        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "msdb");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand("msdb.dbo.sp_add_operator", conn) { CommandType = System.Data.CommandType.StoredProcedure };
            cmd.Parameters.AddWithValue("@name", request.Name);
            cmd.Parameters.AddWithValue("@enabled", request.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@email_address", (object?)request.EmailAddress ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@pager_address", (object?)request.PagerAddress ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return AdminOperationResult.Ok($"Operator '{request.Name}' created.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public async Task<AdminOperationResult> DeleteAgentOperatorAsync(
        ConnectionDefinition connection,
        string? password,
        string operatorName,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "msdb");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand("msdb.dbo.sp_delete_operator", conn) { CommandType = System.Data.CommandType.StoredProcedure };
            cmd.Parameters.AddWithValue("@name", operatorName);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return AdminOperationResult.Ok($"Operator '{operatorName}' deleted.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public async Task<IReadOnlyList<AgentAlertInfo>> GetAgentAlertsAsync(
        ConnectionDefinition connection,
        string? password,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                a.id,
                a.name,
                a.enabled,
                NULLIF(a.severity, 0),
                NULLIF(a.message_id, 0),
                ISNULL(a.database_name, N''),
                ISNULL(CAST(a.notification_message AS nvarchar(4000)), N''),
                ISNULL(j.name, N'')
            FROM msdb.dbo.sysalerts a
            LEFT JOIN msdb.dbo.sysjobs j ON j.job_id = a.job_id
            ORDER BY a.name;
            """;
        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "msdb");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var list = new List<AgentAlertInfo>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(new AgentAlertInfo
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Enabled = reader.GetByte(2) == 1,
                    Severity = reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3)),
                    MessageId = reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetValue(4)),
                    DatabaseName = reader.GetString(5),
                    NotificationMessage = reader.GetString(6),
                    JobName = reader.GetString(7)
                });
            }

            return list;
        }
        catch
        {
            return [];
        }
    }

    public async Task<AdminOperationResult> CreateAgentAlertAsync(
        ConnectionDefinition connection,
        string? password,
        CreateAgentAlertRequest request,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return AdminOperationResult.Fail("Alert name is required.");
        }

        if (request.Severity is null && request.MessageId is null)
        {
            return AdminOperationResult.Fail("Specify severity or message id.");
        }

        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "msdb");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand("msdb.dbo.sp_add_alert", conn) { CommandType = System.Data.CommandType.StoredProcedure };
            cmd.Parameters.AddWithValue("@name", request.Name);
            cmd.Parameters.AddWithValue("@enabled", request.Enabled ? 1 : 0);
            if (request.Severity is int sev)
            {
                cmd.Parameters.AddWithValue("@severity", sev);
            }

            if (request.MessageId is int mid)
            {
                cmd.Parameters.AddWithValue("@message_id", mid);
            }

            if (!string.IsNullOrWhiteSpace(request.DatabaseName))
            {
                cmd.Parameters.AddWithValue("@database_name", request.DatabaseName);
            }

            if (!string.IsNullOrWhiteSpace(request.JobNameToRun))
            {
                cmd.Parameters.AddWithValue("@job_name", request.JobNameToRun);
            }

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(request.NotifyOperatorName))
            {
                await using var notify = new SqlCommand("msdb.dbo.sp_add_notification", conn) { CommandType = System.Data.CommandType.StoredProcedure };
                notify.Parameters.AddWithValue("@alert_name", request.Name);
                notify.Parameters.AddWithValue("@operator_name", request.NotifyOperatorName);
                notify.Parameters.AddWithValue("@notification_method", 1); // email
                await notify.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            sw.Stop();
            return AdminOperationResult.Ok($"Alert '{request.Name}' created.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public async Task<AdminOperationResult> DeleteAgentAlertAsync(
        ConnectionDefinition connection,
        string? password,
        string alertName,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = SqlServerConnectionFactory.Create(connection, password, "msdb");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand("msdb.dbo.sp_delete_alert", conn) { CommandType = System.Data.CommandType.StoredProcedure };
            cmd.Parameters.AddWithValue("@name", alertName);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return AdminOperationResult.Ok($"Alert '{alertName}' deleted.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return AdminOperationResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    private static async Task AddJobStepAsync(
        SqlConnection conn,
        string jobName,
        AgentJobStepRequest step,
        int stepId,
        CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand("msdb.dbo.sp_add_jobstep", conn) { CommandType = System.Data.CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@job_name", jobName);
        cmd.Parameters.AddWithValue("@step_id", stepId);
        cmd.Parameters.AddWithValue("@step_name", string.IsNullOrWhiteSpace(step.StepName) ? $"Step {stepId}" : step.StepName);
        cmd.Parameters.AddWithValue("@subsystem", string.IsNullOrWhiteSpace(step.Subsystem) ? "TSQL" : step.Subsystem);
        cmd.Parameters.AddWithValue("@command", step.Command ?? string.Empty);
        cmd.Parameters.AddWithValue("@database_name", string.IsNullOrWhiteSpace(step.DatabaseName) ? "master" : step.DatabaseName);
        cmd.Parameters.AddWithValue("@on_success_action", step.OnSuccessAction);
        cmd.Parameters.AddWithValue("@on_fail_action", step.OnFailAction);
        if (step.OnSuccessStepId > 0)
        {
            cmd.Parameters.AddWithValue("@on_success_step_id", step.OnSuccessStepId);
        }

        if (step.OnFailStepId > 0)
        {
            cmd.Parameters.AddWithValue("@on_fail_step_id", step.OnFailStepId);
        }

        cmd.Parameters.AddWithValue("@retry_attempts", step.RetryAttempts);
        cmd.Parameters.AddWithValue("@retry_interval", step.RetryIntervalMinutes);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AttachScheduleAsync(
        SqlConnection conn,
        string jobName,
        AgentScheduleRequest schedule,
        CancellationToken cancellationToken)
    {
        var start = schedule.StartAt.LocalDateTime;
        var startDate = start.Year * 10000 + start.Month * 100 + start.Day;
        var startTime = start.Hour * 10000 + start.Minute * 100 + start.Second;
        int endDate = 99991231;
        int endTime = 235959;
        if (schedule.EndAt is { } end)
        {
            var e = end.LocalDateTime;
            endDate = e.Year * 10000 + e.Month * 100 + e.Day;
            endTime = e.Hour * 10000 + e.Minute * 100 + e.Second;
        }

        int freqType;
        int freqInterval;
        int freqSubdayType = 1; // at the specified time
        int freqSubdayInterval = 0;
        int freqRelativeInterval = 0;
        int freqRecurrenceFactor = 0;

        switch ((schedule.Frequency ?? "Daily").Trim().ToLowerInvariant())
        {
            case "once":
                freqType = 1;
                freqInterval = 0;
                break;
            case "weekly":
                freqType = 8;
                freqInterval = schedule.WeeklyDaysMask <= 0 ? 62 : schedule.WeeklyDaysMask;
                freqRecurrenceFactor = 1;
                break;
            default:
                freqType = 4; // daily
                freqInterval = Math.Max(1, schedule.DailyInterval);
                break;
        }

        var scheduleName = string.IsNullOrWhiteSpace(schedule.Name)
            ? $"{jobName}_sched_{start:yyyyMMddHHmm}"
            : schedule.Name;

        await using (var add = new SqlCommand("msdb.dbo.sp_add_schedule", conn) { CommandType = System.Data.CommandType.StoredProcedure })
        {
            add.Parameters.AddWithValue("@schedule_name", scheduleName);
            add.Parameters.AddWithValue("@enabled", schedule.Enabled ? 1 : 0);
            add.Parameters.AddWithValue("@freq_type", freqType);
            add.Parameters.AddWithValue("@freq_interval", freqInterval);
            add.Parameters.AddWithValue("@freq_subday_type", freqSubdayType);
            add.Parameters.AddWithValue("@freq_subday_interval", freqSubdayInterval);
            add.Parameters.AddWithValue("@freq_relative_interval", freqRelativeInterval);
            add.Parameters.AddWithValue("@freq_recurrence_factor", freqRecurrenceFactor);
            add.Parameters.AddWithValue("@active_start_date", startDate);
            add.Parameters.AddWithValue("@active_end_date", endDate);
            add.Parameters.AddWithValue("@active_start_time", startTime);
            add.Parameters.AddWithValue("@active_end_time", endTime);
            await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var attach = new SqlCommand("msdb.dbo.sp_attach_schedule", conn) { CommandType = System.Data.CommandType.StoredProcedure })
        {
            attach.Parameters.AddWithValue("@job_name", jobName);
            attach.Parameters.AddWithValue("@schedule_name", scheduleName);
            await attach.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
