namespace PSMS.Core.Models;

public sealed record DatabaseInfo(string Name);

public sealed record SchemaInfo(string Name);

public sealed record TableInfo(string Schema, string Name);

public sealed record ViewInfo(string Schema, string Name);

public sealed record ProcedureInfo(string Schema, string Name);

public sealed record FunctionInfo(string Schema, string Name);

public sealed record ColumnInfo(
    string Schema,
    string Table,
    string Name,
    string DataType,
    bool IsNullable,
    int? MaxLength,
    int OrdinalPosition);

public sealed record IndexInfo(
    string Name,
    bool IsUnique,
    bool IsPrimaryKey,
    string Type,
    string Columns);

public sealed record ForeignKeyInfo(
    string Name,
    string Columns,
    string ReferencedSchema,
    string ReferencedTable,
    string ReferencedColumns);

public sealed class TableSchemaOverview
{
    public required IReadOnlyList<ColumnInfo> Columns { get; init; }
    public IReadOnlyList<IndexInfo> Indexes { get; init; } = [];
    public IReadOnlyList<ForeignKeyInfo> ForeignKeys { get; init; } = [];
}
