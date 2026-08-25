namespace PSMS.Core.Models.Admin;

// --- Job designer requests -------------------------------------------------

public sealed class AgentJobStepRequest
{
    public int StepId { get; set; }
    public string StepName { get; set; } = "Step 1";
    /// <summary>TSQL, CmdExec, PowerShell, etc.</summary>
    public string Subsystem { get; set; } = "TSQL";
    public string Command { get; set; } = "SELECT 1;";
    public string DatabaseName { get; set; } = "master";
    /// <summary>1=quit success, 2=quit fail, 3=go next, 4=go to step</summary>
    public int OnSuccessAction { get; set; } = 3;
    public int OnFailAction { get; set; } = 2;
    public int OnSuccessStepId { get; set; }
    public int OnFailStepId { get; set; }
    public int RetryAttempts { get; set; }
    public int RetryIntervalMinutes { get; set; }
}

public sealed class AgentScheduleRequest
{
    public string Name { get; set; } = "Schedule";
    public bool Enabled { get; set; } = true;
    /// <summary>Once, Daily, Weekly</summary>
    public string Frequency { get; set; } = "Daily";
    public DateTimeOffset StartAt { get; set; } = DateTimeOffset.Now.AddMinutes(5);
    public DateTimeOffset? EndAt { get; set; }
    /// <summary>For Weekly: bitmask Sun=1 … Sat=64</summary>
    public int WeeklyDaysMask { get; set; } = 62; // Mon–Fri
    public int DailyInterval { get; set; } = 1;
}

public sealed class CreateAgentJobRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = "[Uncategorized]";
    public bool Enabled { get; set; } = true;
    public string OwnerLoginName { get; set; } = string.Empty;
    public List<AgentJobStepRequest> Steps { get; set; } = [];
    public AgentScheduleRequest? Schedule { get; set; }
    /// <summary>Operator name to notify on failure (optional).</summary>
    public string? NotifyOperatorOnFailure { get; set; }
}

public sealed class UpdateAgentJobRequest
{
    public Guid JobId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? NewName { get; set; }
    public string? Description { get; set; }
    public string Category { get; set; } = "[Uncategorized]";
    public bool Enabled { get; set; } = true;
    public List<AgentJobStepRequest> Steps { get; set; } = [];
    public AgentScheduleRequest? Schedule { get; set; }
    public string? NotifyOperatorOnFailure { get; set; }
}

public sealed class AgentJobScheduleInfo
{
    public int ScheduleId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string FrequencyDescription { get; set; } = string.Empty;
    public DateTimeOffset? NextRun { get; set; }
}

public sealed class AgentOperatorInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string EmailAddress { get; set; } = string.Empty;
    public string PagerAddress { get; set; } = string.Empty;
}

public sealed class CreateAgentOperatorRequest
{
    public string Name { get; set; } = string.Empty;
    public string? EmailAddress { get; set; }
    public string? PagerAddress { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class AgentAlertInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int? Severity { get; set; }
    public int? MessageId { get; set; }
    public string DatabaseName { get; set; } = string.Empty;
    public string NotificationMessage { get; set; } = string.Empty;
    public string JobName { get; set; } = string.Empty;
}

public sealed class CreateAgentAlertRequest
{
    public string Name { get; set; } = string.Empty;
    public int? Severity { get; set; }
    public int? MessageId { get; set; }
    public string? DatabaseName { get; set; }
    public bool Enabled { get; set; } = true;
    public string? NotifyOperatorName { get; set; }
    public string? JobNameToRun { get; set; }
}

// --- Activity / security / indexes / DB props ------------------------------

public sealed class BlockingSessionInfo
{
    public int SessionId { get; set; }
    public int BlockingSessionId { get; set; }
    public string LoginName { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public string WaitType { get; set; } = string.Empty;
    public long WaitTimeMs { get; set; }
    public string? SqlText { get; set; }
}

public sealed class ExpensiveQueryInfo
{
    public long QueryHash { get; set; }
    public long ExecutionCount { get; set; }
    public double TotalCpuMs { get; set; }
    public double TotalDurationMs { get; set; }
    public double TotalLogicalReads { get; set; }
    public string? SqlText { get; set; }
}

public sealed class LoginInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsDisabled { get; set; }
    public DateTimeOffset CreateDate { get; set; }
    public string DefaultDatabase { get; set; } = string.Empty;
}

public sealed class CreateLoginRequest
{
    public string LoginName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DefaultDatabase { get; set; } = "master";
    public bool MustChange { get; set; }
    public string? MapToDatabase { get; set; }
    public string? DatabaseUserName { get; set; }
}

public sealed class MissingIndexInfo
{
    public string DatabaseName { get; set; } = string.Empty;
    public string SchemaName { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string EqualityColumns { get; set; } = string.Empty;
    public string InequalityColumns { get; set; } = string.Empty;
    public string IncludedColumns { get; set; } = string.Empty;
    public double Impact { get; set; }
    public long UserSeeks { get; set; }
    public string CreateIndexStatement { get; set; } = string.Empty;
}

public sealed class IndexFragmentationInfo
{
    public string SchemaName { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string IndexName { get; set; } = string.Empty;
    public double AvgFragmentationPercent { get; set; }
    public long PageCount { get; set; }
    public string IndexType { get; set; } = string.Empty;
}

public sealed class DatabaseFileInfo
{
    public string LogicalName { get; set; } = string.Empty;
    public string PhysicalName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public double SizeMb { get; set; }
    public double UsedMb { get; set; }
    public int FileId { get; set; }
}

public sealed class DatabasePropertiesInfo
{
    public string Name { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string RecoveryModel { get; set; } = string.Empty;
    public string Collation { get; set; } = string.Empty;
    public int CompatibilityLevel { get; set; }
    public bool IsReadOnly { get; set; }
    public bool AutoClose { get; set; }
    public bool AutoShrink { get; set; }
    public IReadOnlyList<DatabaseFileInfo> Files { get; set; } = [];
}
