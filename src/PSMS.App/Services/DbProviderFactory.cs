using PSMS.Core.Abstractions;
using PSMS.Core.Models;

namespace PSMS.App.Services;

public sealed class DbProviderFactory : IDbProviderFactory
{
    private readonly IReadOnlyDictionary<DbEngine, IDbProvider> _providers;

    public DbProviderFactory(IEnumerable<IDbProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.Engine);
    }

    public IEnumerable<DbEngine> RegisteredEngines => _providers.Keys;

    public IDbProvider GetProvider(DbEngine engine)
    {
        if (_providers.TryGetValue(engine, out var provider))
        {
            return provider;
        }

        throw new NotSupportedException($"Database engine '{engine}' is not registered yet.");
    }
}
