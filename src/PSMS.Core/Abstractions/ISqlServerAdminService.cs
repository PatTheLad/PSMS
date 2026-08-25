using PSMS.Core.Models;
using PSMS.Core.Models.Admin;

namespace PSMS.Core.Abstractions;

/// <summary>
/// SQL Server administration surface (create DB, backup/restore, Agent, analysis).
/// Implemented with T-SQL so it works on Windows and Linux SQL Server instances.
/// </summary>
public interface ISqlServerAdminService
{
    Task<IReadOnlyList<DatabaseAdminInfo>> GetDatabaseAdminInfoAsync(
        ConnectionDefinition connection,
        string? password,
        CancellationToken cancellationToken = default);

    Task<AdminOperationResult> CreateDatabaseAsync(
        ConnectionDefinition connection,
        string? password,
        CreateDatabaseRequest request,
        CancellationToken cancellationToken = default);

    Task<AdminOperationResult> BackupDatabaseAsync(
        ConnectionDefinition connection,
        string? password,
        BackupDatabaseRequest request,
        CancellationToken cancellationToken = default);

    Task<AdminOperationResult> RestoreDatabaseAsync(
        ConnectionDefinition connection,
        string? password,
        RestoreDatabaseRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackupSetInfo>> GetRecentBackupsAsync(
        ConnectionDefinition connection,
        string? password,
        string? database = null,
        CancellationToken cancellationToken = default);

    Task<string> SuggestBackupPathAsync(
        ConnectionDefinition connection,
        string? password,
        string database,
        CancellationToken cancellationToken = default);

    Task<AgentServiceStatus> GetAgentStatusAsync(
        ConnectionDefinition connection,
        string? password,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentJobInfo>> GetAgentJobsAsync(
        ConnectionDefinition connection,
        string? password,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentJobStepInfo>> GetAgentJobStepsAsync(
        ConnectionDefinition connection,
        string? password,
        Guid jobId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentJobHistoryEntry>> GetAgentJobHistoryAsync(
        ConnectionDefinition connection,
        string? password,
        Guid jobId,
        int maxRows = 50,
        CancellationToken cancellationToken = default);

    Task<AdminOperationResult> StartAgentJobAsync(
        ConnectionDefinition connection,
        string? password,
        string jobName,
        CancellationToken cancellationToken = default);

    Task<AdminOperationResult> StopAgentJobAsync(
        ConnectionDefinition connection,
        string? password,
        string jobName,
        CancellationToken cancellationToken = default);

    Task<AdminOperationResult> SetAgentJobEnabledAsync(
        ConnectionDefinition connection,
        string? password,
        string jobName,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<AdminOperationResult> CreateAgentJobAsync(
        ConnectionDefinition connection,
        string? password,
        CreateAgentJobRequest request,
        CancellationToken cancellationToken = default);

    Task<AdminOperationResult> DropDatabaseAsync(
        ConnectionDefinition connection,
        string? password,
        string database,
        bool force = true,
        CancellationToken cancellationToken = default);

    Task<AdminOperationResult> SetRecoveryModelAsync(
        ConnectionDefinition connection,
        string? password,
        string database,
        string recoveryModel,
        CancellationToken cancellationToken = default);

    Task<AdminOperationResult> KillSessionAsync(
        ConnectionDefinition connection,
        string? password,
        int sessionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MissingIndexInfo>> GetMissingIndexesAsync(
        ConnectionDefinition connection,
        string? password,
        int top = 25,
        CancellationToken cancellationToken = default);

    Task<ServerAnalysisSnapshot> GetServerAnalysisAsync(
        ConnectionDefinition connection,
        string? password,
        CancellationToken cancellationToken = default);
}
