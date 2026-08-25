namespace PSMS.Core.Models;

public sealed class QueryHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset ExecutedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Sql { get; set; } = string.Empty;
    public Guid ConnectionId { get; set; }
    public string ConnectionName { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public long ElapsedMilliseconds { get; set; }
    public bool Succeeded { get; set; }
    public int RowCount { get; set; }
    public string? Error { get; set; }
}
