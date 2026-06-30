namespace Subiekt.Bridge.Infrastructure.Sql;

/// <summary>
/// Connection settings for the read-only SQL adapters. This is deliberately a
/// SEPARATE connection from the Sfera write session: reads use their own
/// connection string so listing data never contends with the Sfera write lock.
/// <para>
/// Either set <see cref="ConnectionString"/> directly, or leave it empty and let
/// the composition root populate the discrete <c>Server</c>/<c>Database</c>/auth
/// fields (e.g. from the existing Sfera <c>Sql*</c> settings); the factory builds
/// the connection string from those when <see cref="ConnectionString"/> is blank.
/// </para>
/// </summary>
public sealed class SqlReadOptions
{
    /// <summary>Full connection string. When set, the discrete fields are ignored.</summary>
    public string ConnectionString { get; set; } = "";

    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public bool UseWindowsAuth { get; set; } = true;
    public bool Encrypt { get; set; } = true;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>
    /// Hard upper bound applied to every <c>limit</c> the read adapters accept,
    /// so a caller cannot ask for an unbounded result set.
    /// </summary>
    public int MaxLimit { get; set; } = 1000;
}
