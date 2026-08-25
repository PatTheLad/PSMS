namespace PSMS.Core.Models;

public sealed class SavedSnippet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Snippet";
    public string Sql { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
