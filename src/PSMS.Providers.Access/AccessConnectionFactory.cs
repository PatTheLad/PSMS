using System.Data.Odbc;
using System.Runtime.InteropServices;
using PSMS.Core.Models;

namespace PSMS.Providers.Access;

internal static class AccessConnectionFactory
{
    public static void EnsureWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException(
                "Microsoft Access (ODBC) is only supported on Windows. " +
                "Install the Microsoft Access Database Engine and use PSMS on Windows to connect to .mdb/.accdb files.");
        }
    }

    public static string ResolveFilePath(ConnectionDefinition connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.Database))
        {
            return connection.Database.Trim();
        }

        if (!string.IsNullOrWhiteSpace(connection.Server))
        {
            return connection.Server.Trim();
        }

        throw new InvalidOperationException("Access connection requires a file path in Database (preferred) or Server.");
    }

    public static string DatabaseDisplayName(ConnectionDefinition connection)
    {
        var path = ResolveFilePath(connection);
        var fileName = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(fileName) ? "Access" : fileName;
    }

    public static OdbcConnection Create(ConnectionDefinition connection, string? password)
    {
        EnsureWindows();

        var path = ResolveFilePath(connection);
        var user = string.IsNullOrWhiteSpace(connection.UserName) ? "Admin" : connection.UserName.Trim();
        var pwd = password ?? string.Empty;

        // Prefer ACE driver; Jet remains for legacy .mdb when ACE is unavailable.
        var connectionString =
            $"Driver={{Microsoft Access Driver (*.mdb, *.accdb)}};Dbq={path};Uid={user};Pwd={pwd};";

        return new OdbcConnection(connectionString);
    }
}
