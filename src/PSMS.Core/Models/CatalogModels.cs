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
