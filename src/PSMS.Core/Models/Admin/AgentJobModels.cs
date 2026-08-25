namespace PSMS.Core.Models.Admin;

public sealed class AgentServiceStatus
{
    public bool AgentAvailable { get; init; }
    public bool AgentRunning { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public string? Detail { get; init; }
}

public sealed class AgentJobInfo
{
    public Guid JobId { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Owner { get; init; } = string.Empty;
    public string CurrentStatus { get; init; } = string.Empty;
    public string LastRunOutcome { get; init; } = string.Empty;
    public DateTimeOffset? LastRun { get; init; }
    public DateTimeOffset? NextRun { get; init; }
    public string Description { get; init; } = string.Empty;
    public int StepCount { get; init; }
}

public sealed class AgentJobHistoryEntry
{
    public long InstanceId { get; init; }
    public string StepName { get; init; } = string.Empty;
    public int StepId { get; init; }
    public string RunStatus { get; init; } = string.Empty;
    public DateTimeOffset RunDate { get; init; }
    public string RunDuration { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed class AgentJobStepInfo
{
    public int StepId { get; init; }
    public string StepName { get; init; } = string.Empty;
    public string Subsystem { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public string OnSuccessAction { get; init; } = string.Empty;
    public string OnFailAction { get; init; } = string.Empty;
}

public sealed class ServerAnalysisSnapshot
{
    public string ServerName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Edition { get; init; } = string.Empty;
    public string ProductLevel { get; init; } = string.Empty;
    public int CpuCount { get; init; }
    public double PhysicalMemoryMb { get; init; }
    public DateTimeOffset ServerStartTime { get; init; }
    public int UserDatabaseCount { get; init; }
    public double TotalDataSizeMb { get; init; }
    public double TotalLogSizeMb { get; init; }
    public AgentServiceStatus Agent { get; init; } = new();
    public IReadOnlyList<DatabaseAdminInfo> Databases { get; init; } = [];
    public IReadOnlyList<SessionActivityInfo> TopSessions { get; init; } = [];
    public IReadOnlyList<WaitStatInfo> TopWaits { get; init; } = [];
    public IReadOnlyList<MissingIndexInfo> MissingIndexes { get; init; } = [];
}

public sealed class SessionActivityInfo
{
    public int SessionId { get; init; }
    public string LoginName { get; init; } = string.Empty;
    public string HostName { get; init; } = string.Empty;
    public string Database { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public string WaitType { get; init; } = string.Empty;
    public long CpuTimeMs { get; init; }
    public long ElapsedTimeMs { get; init; }
    public string? SqlText { get; init; }
}

public sealed class WaitStatInfo
{
    public string WaitType { get; init; } = string.Empty;
    public long WaitingTasksCount { get; init; }
    public double WaitTimeSec { get; init; }
    public double SignalWaitTimeSec { get; init; }
}
