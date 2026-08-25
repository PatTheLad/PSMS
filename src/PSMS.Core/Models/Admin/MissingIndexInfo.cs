namespace PSMS.Core.Models.Admin;

public sealed class MissingIndexInfo
{
    public string Database { get; init; } = string.Empty;
    public string Schema { get; init; } = string.Empty;
    public string Table { get; init; } = string.Empty;
    public double ImpactScore { get; init; }
    public long UserSeeks { get; init; }
    public long UserScans { get; init; }
    public string EqualityColumns { get; init; } = string.Empty;
    public string InequalityColumns { get; init; } = string.Empty;
    public string IncludedColumns { get; init; } = string.Empty;
    public string CreateIndexStatement { get; init; } = string.Empty;
}

public sealed class CreateAgentJobRequest
{
    public string JobName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string StepName { get; set; } = "Step 1";
    public string DatabaseName { get; set; } = "master";
    public string Command { get; set; } = "SELECT 1;";
    public bool Enabled { get; set; } = true;
    public bool StartImmediately { get; set; }
}
