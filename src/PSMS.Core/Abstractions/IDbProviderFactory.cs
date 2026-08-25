using PSMS.Core.Models;

namespace PSMS.Core.Abstractions;

public interface IDbProviderFactory
{
    IDbProvider GetProvider(DbEngine engine);
    IEnumerable<DbEngine> RegisteredEngines { get; }
}
