using PSMS.Core.Abstractions;
using PSMS.Core.Models;
using PSMS.Core.Sql;

namespace PSMS.App.Services;

public sealed class QueryRunner
{
    private readonly IDbProviderFactory _factory;
    private readonly IConnectionStore _store;
    private CancellationTokenSource? _cts;

    public QueryRunner(IDbProviderFactory factory, IConnectionStore store)
    {
        _factory = factory;
        _store = store;
    }

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignored
        }
    }

    public async Task<QueryResult> ExecuteAsync(
        ConnectionDefinition connection,
        string database,
        string sql,
        CancellationToken externalToken = default)
    {
        Cancel();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var token = _cts.Token;

        var password = connection.UseWindowsAuth ? null : _store.DecryptPassword(connection);
        var provider = _factory.GetProvider(connection.Engine);
        var batches = SqlBatchSplitter.Split(sql);

        var allSets = new List<ResultSet>();
        var messages = new List<string>();
        var rowsAffected = 0;
        long elapsed = 0;
        string? error = null;

        try
        {
            for (var i = 0; i < batches.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                messages.Add($"--- Batch {i + 1} of {batches.Count} ---");
                var batchResult = await provider.ExecuteQueryAsync(connection, password, database, batches[i], 10_000, token)
                    .ConfigureAwait(false);

                allSets.AddRange(batchResult.ResultSets);
                messages.AddRange(batchResult.Messages);
                rowsAffected += batchResult.RowsAffected;
                elapsed += batchResult.ElapsedMilliseconds;

                if (!string.IsNullOrEmpty(batchResult.Error))
                {
                    error = batchResult.Error;
                    messages.Add($"Batch {i + 1} failed: {batchResult.Error}");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new QueryResult
            {
                ResultSets = allSets,
                Messages = messages.Concat(["Query cancelled."]).ToList(),
                ElapsedMilliseconds = elapsed,
                RowsAffected = rowsAffected,
                Error = "Cancelled"
            };
        }

        return new QueryResult
        {
            ResultSets = allSets,
            Messages = messages,
            ElapsedMilliseconds = elapsed,
            RowsAffected = rowsAffected,
            Error = error
        };
    }
}
