namespace PSMS.Core.Models;

public sealed class ResultSet
{
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; } = [];
    /// <summary>Pre-formatted display strings (same shape as Rows). Prefer this in the UI.</summary>
    public IReadOnlyList<IReadOnlyList<string?>> DisplayRows { get; init; } = [];
    public bool Truncated { get; init; }
}

/// <summary>When set, ResultsPane allows cell edits and can script UPDATEs.</summary>
public sealed class EditableResultContext
{
    public required Guid ConnectionId { get; init; }
    public required string Database { get; init; }
    public required string Schema { get; init; }
    public required string Table { get; init; }
    public required DbEngine Engine { get; init; }
    public IReadOnlyList<string> KeyColumns { get; init; } = [];
}

public sealed class QueryResult
{
    public IReadOnlyList<ResultSet> ResultSets { get; init; } = [];
    public IReadOnlyList<string> Messages { get; init; } = [];
    public long ElapsedMilliseconds { get; init; }
    public int RowsAffected { get; init; }
    public string? Error { get; init; }
    /// <summary>Estimated or actual execution plan grid (SHOWPLAN / STATISTICS PROFILE).</summary>
    public bool IsExecutionPlan { get; init; }
    public EditableResultContext? EditContext { get; set; }

    public bool HasResultSet => ResultSets.Count > 0;

    public IReadOnlyList<string> Columns => ResultSets.Count > 0 ? ResultSets[0].Columns : [];
    public IReadOnlyList<IReadOnlyList<object?>> Rows => ResultSets.Count > 0 ? ResultSets[0].Rows : [];
}
