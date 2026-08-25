namespace PSMS.Core.Models;

public enum CatalogObjectKind
{
    Schema,
    Table,
    View,
    Procedure,
    Function,
    Column
}

public sealed record CatalogObjectInfo(
    string Schema,
    string Name,
    CatalogObjectKind Kind,
    string? DataType = null,
    string? Database = null);
