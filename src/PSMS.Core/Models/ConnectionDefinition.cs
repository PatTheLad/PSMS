namespace PSMS.Core.Models;

public sealed class ConnectionDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DbEngine Engine { get; set; } = DbEngine.SqlServer;
    public string Server { get; set; } = "localhost";
    public string? Database { get; set; }
    public bool UseWindowsAuth { get; set; } = true;
    public string? UserName { get; set; }
    /// <summary>Encrypted password payload (Base64). Never store plaintext.</summary>
    public string? EncryptedPassword { get; set; }
    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; } = true;
    public int? Port { get; set; }
    /// <summary>Optional accent color (CSS hex, e.g. #3b9eff) for tabs and status bar.</summary>
    public string? Color { get; set; }
}
