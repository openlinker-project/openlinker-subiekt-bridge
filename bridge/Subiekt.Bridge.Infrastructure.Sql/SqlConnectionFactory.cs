using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Subiekt.Bridge.Infrastructure.Sql;

/// <summary>
/// Default <see cref="ISqlConnectionFactory"/> backed by
/// <see cref="Microsoft.Data.SqlClient"/>. Builds the connection string once from
/// <see cref="SqlReadOptions"/>: uses <see cref="SqlReadOptions.ConnectionString"/>
/// verbatim when set, otherwise composes one from the discrete server/database/auth
/// fields.
/// </summary>
public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(SqlReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = BuildConnectionString(options);
    }

    public DbConnection Create() => new SqlConnection(_connectionString);

    private static string BuildConnectionString(SqlReadOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
            return options.ConnectionString;

        if (string.IsNullOrWhiteSpace(options.Server) || string.IsNullOrWhiteSpace(options.Database))
            throw new InvalidOperationException(
                "SqlReadOptions requires either ConnectionString or both Server and Database.");

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = options.Server,
            InitialCatalog = options.Database,
            Encrypt = options.Encrypt,
            // Subiekt installs typically use a self-signed cert on a local/LAN
            // instance; mirror the legacy behaviour of trusting it.
            TrustServerCertificate = true,
            // NOTE: lock isolation from the Sfera write session comes purely from
            // using this SEPARATE connection. We deliberately do NOT set
            // ApplicationIntent=ReadOnly: a standalone Subiekt instance ignores it,
            // and against an AlwaysOn AG without a readable secondary it would make
            // the connection fail. Make it opt-in via SqlReadOptions if a read
            // replica ever exists.
        };

        if (options.UseWindowsAuth)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = options.User;
            builder.Password = options.Password;
        }

        return builder.ConnectionString;
    }
}
