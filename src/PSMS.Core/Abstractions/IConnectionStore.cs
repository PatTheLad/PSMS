using PSMS.Core.Models;

namespace PSMS.Core.Abstractions;

public interface IConnectionStore
{
    Task<IReadOnlyList<ConnectionDefinition>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ConnectionDefinition connection, string? plaintextPassword, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    string? DecryptPassword(ConnectionDefinition connection);
}
