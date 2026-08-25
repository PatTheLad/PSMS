using PSMS.Core.Models;

namespace PSMS.Core.Abstractions;

public interface IDbProvider
{
    DbEngine Engine { get; }

    Task TestConnectionAsync(ConnectionDefinition connection, string? password, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DatabaseInfo>> GetDatabasesAsync(ConnectionDefinition connection, string? password, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SchemaInfo>> GetSchemasAsync(ConnectionDefinition connection, string? password, string database, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TableInfo>> GetTablesAsync(ConnectionDefinition connection, string? password, string database, string schema, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ViewInfo>> GetViewsAsync(ConnectionDefinition connection, string? password, string database, string schema, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ConnectionDefinition connection, string? password, string database, string schema, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FunctionInfo>> GetFunctionsAsync(ConnectionDefinition connection, string? password, string database, string schema, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(ConnectionDefinition connection, string? password, string database, string schema, string table, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CatalogObjectInfo>> GetCatalogObjectsAsync(ConnectionDefinition connection, string? password, string database, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ColumnInfo>> GetAllColumnsAsync(ConnectionDefinition connection, string? password, string database, CancellationToken cancellationToken = default);

    Task<string?> GetObjectDefinitionAsync(ConnectionDefinition connection, string? password, string database, string schema, string name, CancellationToken cancellationToken = default);

    Task<string> ScriptCreateTableAsync(ConnectionDefinition connection, string? password, string database, string schema, string table, CancellationToken cancellationToken = default);

    Task<QueryResult> ExecuteQueryAsync(
        ConnectionDefinition connection,
        string? password,
        string database,
        string sql,
        int maxRows = 10_000,
        CancellationToken cancellationToken = default);
}
