using Microsoft.Data.SqlClient;
using PSMS.Core.Models;

namespace PSMS.Providers.SqlServer;

internal static class SqlServerConnectionFactory
{
    public static SqlConnection Create(ConnectionDefinition connection, string? password, string? databaseOverride = null)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = connection.Port is > 0 ? $"{connection.Server},{connection.Port}" : connection.Server,
            InitialCatalog = string.IsNullOrWhiteSpace(databaseOverride)
                ? (connection.Database ?? "master")
                : databaseOverride,
            Encrypt = connection.Encrypt,
            TrustServerCertificate = connection.TrustServerCertificate,
            PersistSecurityInfo = false,
            ApplicationName = "PSMS",
            ConnectTimeout = 30,
            CommandTimeout = 0
        };

        if (connection.UseWindowsAuth)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = connection.UserName ?? string.Empty;
            builder.Password = password ?? string.Empty;
        }

        return new SqlConnection(builder.ConnectionString);
    }
}
