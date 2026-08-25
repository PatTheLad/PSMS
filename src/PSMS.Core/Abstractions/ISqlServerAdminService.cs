using PSMS.Core.Models;
using PSMS.Core.Models.Admin;

namespace PSMS.Core.Abstractions;

/// <summary>
/// SQL Server administration surface (create DB, backup/restore, Agent, analysis, security, indexes).
/// Implemented with T-SQL so it works on Windows and Linux SQL Server instances.
/// </summary>
public interface ISqlServerAdminService
{
    Task<IReadOnlyList<DatabaseAdminInfo>> GetDatabaseAdminInfoAsync(
        ConnectionDefinition connection, string? password, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> CreateDatabaseAsync(
        ConnectionDefinition connection, string? password, CreateDatabaseRequest request, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> BackupDatabaseAsync(
        ConnectionDefinition connection, string? password, BackupDatabaseRequest request, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> RestoreDatabaseAsync(
        ConnectionDefinition connection, string? password, RestoreDatabaseRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackupSetInfo>> GetRecentBackupsAsync(
        ConnectionDefinition connection, string? password, string? database = null, CancellationToken cancellationToken = default);

    Task<string> SuggestBackupPathAsync(
        ConnectionDefinition connection, string? password, string database, CancellationToken cancellationToken = default);

    Task<AgentServiceStatus> GetAgentStatusAsync(
        ConnectionDefinition connection, string? password, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentJobInfo>> GetAgentJobsAsync(
        ConnectionDefinition connection, string? password, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentJobStepInfo>> GetAgentJobStepsAsync(
        ConnectionDefinition connection, string? password, Guid jobId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentJobHistoryEntry>> GetAgentJobHistoryAsync(
        ConnectionDefinition connection, string? password, Guid jobId, int maxRows = 50, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentJobScheduleInfo>> GetAgentJobSchedulesAsync(
        ConnectionDefinition connection, string? password, Guid jobId, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> StartAgentJobAsync(
        ConnectionDefinition connection, string? password, string jobName, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> StopAgentJobAsync(
        ConnectionDefinition connection, string? password, string jobName, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> SetAgentJobEnabledAsync(
        ConnectionDefinition connection, string? password, string jobName, bool enabled, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> CreateAgentJobAsync(
        ConnectionDefinition connection, string? password, CreateAgentJobRequest request, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> UpdateAgentJobAsync(
        ConnectionDefinition connection, string? password, UpdateAgentJobRequest request, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> DeleteAgentJobAsync(
        ConnectionDefinition connection, string? password, string jobName, CancellationToken cancellationToken = default);

    Task<string> ScriptAgentJobAsync(
        ConnectionDefinition connection, string? password, Guid jobId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentOperatorInfo>> GetAgentOperatorsAsync(
        ConnectionDefinition connection, string? password, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> CreateAgentOperatorAsync(
        ConnectionDefinition connection, string? password, CreateAgentOperatorRequest request, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> DeleteAgentOperatorAsync(
        ConnectionDefinition connection, string? password, string operatorName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentAlertInfo>> GetAgentAlertsAsync(
        ConnectionDefinition connection, string? password, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> CreateAgentAlertAsync(
        ConnectionDefinition connection, string? password, CreateAgentAlertRequest request, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> DeleteAgentAlertAsync(
        ConnectionDefinition connection, string? password, string alertName, CancellationToken cancellationToken = default);

    Task<ServerAnalysisSnapshot> GetServerAnalysisAsync(
        ConnectionDefinition connection, string? password, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BlockingSessionInfo>> GetBlockingSessionsAsync(
        ConnectionDefinition connection, string? password, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExpensiveQueryInfo>> GetExpensiveQueriesAsync(
        ConnectionDefinition connection, string? password, int top = 25, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> KillSessionAsync(
        ConnectionDefinition connection, string? password, int sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LoginInfo>> GetLoginsAsync(
        ConnectionDefinition connection, string? password, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> CreateLoginAsync(
        ConnectionDefinition connection, string? password, CreateLoginRequest request, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> DropLoginAsync(
        ConnectionDefinition connection, string? password, string loginName, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> SetLoginEnabledAsync(
        ConnectionDefinition connection, string? password, string loginName, bool enabled, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MissingIndexInfo>> GetMissingIndexesAsync(
        ConnectionDefinition connection, string? password, string? database = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IndexFragmentationInfo>> GetIndexFragmentationAsync(
        ConnectionDefinition connection, string? password, string database, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> RebuildIndexAsync(
        ConnectionDefinition connection, string? password, string database, string schema, string table, string indexName, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> ReorganizeIndexAsync(
        ConnectionDefinition connection, string? password, string database, string schema, string table, string indexName, CancellationToken cancellationToken = default);

    Task<DatabasePropertiesInfo?> GetDatabasePropertiesAsync(
        ConnectionDefinition connection, string? password, string database, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> SetDatabaseOnlineAsync(
        ConnectionDefinition connection, string? password, string database, bool online, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> SetDatabaseReadOnlyAsync(
        ConnectionDefinition connection, string? password, string database, bool readOnly, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> ShrinkDatabaseAsync(
        ConnectionDefinition connection, string? password, string database, int targetPercentFree = 10, CancellationToken cancellationToken = default);
}
