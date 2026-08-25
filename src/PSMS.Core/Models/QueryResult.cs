namespace PSMS.Core.Models;

public sealed class ResultSet
{
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; } = [];
    public bool Truncated { get; init; }
}

public sealed class QueryResult
{
    public IReadOnlyList<ResultSet> ResultSets { get; init; } = [];
    public IReadOnlyList<string> Messages { get; init; } = [];
    public long ElapsedMilliseconds { get; init; }
    public int RowsAffected { get; init; }
    public string? Error { get; init; }
    /// <summary>SQL Server SHOWPLAN / STATISTICS XML when requested.</summary>
    public string? ExecutionPlanXml { get; init; }
    public IReadOnlyList<PlanNodeSummary> PlanNodes { get; init; } = [];

    public bool HasResultSet => ResultSets.Count > 0;
    public bool HasExecutionPlan => !string.IsNullOrWhiteSpace(ExecutionPlanXml);

    public IReadOnlyList<string> Columns => ResultSets.Count > 0 ? ResultSets[0].Columns : [];
    public IReadOnlyList<IReadOnlyList<object?>> Rows => ResultSets.Count > 0 ? ResultSets[0].Rows : [];
}

public sealed class PlanNodeSummary
{
    public int NodeId { get; init; }
    public string PhysicalOp { get; init; } = string.Empty;
    public string LogicalOp { get; init; } = string.Empty;
    public double EstimatedRows { get; init; }
    public double EstimatedCost { get; init; }
    public string? ObjectName { get; init; }
}

