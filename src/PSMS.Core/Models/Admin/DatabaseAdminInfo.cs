namespace PSMS.Core.Models.Admin;

public sealed class DatabaseAdminInfo
{
    public string Name { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string RecoveryModel { get; init; } = string.Empty;
    public string Collation { get; init; } = string.Empty;
    public double DataSizeMb { get; init; }
    public double LogSizeMb { get; init; }
    public DateTimeOffset? LastFullBackup { get; init; }
    public DateTimeOffset? LastLogBackup { get; init; }
    public bool IsSystem { get; init; }
    public int CompatibilityLevel { get; init; }
    public DateTimeOffset CreateDate { get; init; }
}

public sealed class CreateDatabaseRequest
{
    public string Name { get; set; } = string.Empty;
    public string RecoveryModel { get; set; } = "SIMPLE";
    public string? Collation { get; set; }
    public int InitialDataSizeMb { get; set; } = 8;
    public int InitialLogSizeMb { get; set; } = 8;
}

public sealed class BackupDatabaseRequest
{
    public string Database { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public bool CopyOnly { get; set; }
    public bool Compress { get; set; } = true;
    public bool Init { get; set; } = true;
    public bool Verify { get; set; } = true;
    public string? Description { get; set; }
}

public sealed class RestoreDatabaseRequest
{
    public string Database { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public bool Replace { get; set; }
    public bool Recover { get; set; } = true;
}

public sealed class BackupSetInfo
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public DateTimeOffset BackupStartDate { get; init; }
    public double BackupSizeMb { get; init; }
    public string? PhysicalDeviceName { get; init; }
    public string? Description { get; init; }
}
