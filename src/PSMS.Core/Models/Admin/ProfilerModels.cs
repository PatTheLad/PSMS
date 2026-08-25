namespace PSMS.Core.Models.Admin;

public sealed class XEventSessionInfo
{
    public string Name { get; set; } = string.Empty;
    public bool IsRunning { get; set; }
    public string Target { get; set; } = string.Empty;
    public DateTimeOffset? CreateTime { get; set; }
}

public sealed class XEventTemplate
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class StartProfilerSessionRequest
{
    public string SessionName { get; set; } = "psms_profiler";
    public string TemplateId { get; set; } = "tsql_duration";
    public string? DatabaseName { get; set; }
    public string? LoginName { get; set; }
    public string? ApplicationName { get; set; }
    public int MinDurationMs { get; set; }
    public bool ExcludeSystem { get; set; } = true;
    public int MaxMemoryKb { get; set; } = 4096;
}

public sealed class ProfilerEvent
{
    public DateTimeOffset Timestamp { get; set; }
    public string EventName { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string LoginName { get; set; } = string.Empty;
    public string ClientApp { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public long CpuMs { get; set; }
    public long Reads { get; set; }
    public long Writes { get; set; }
    public int SessionId { get; set; }
    public string? SqlText { get; set; }
}
