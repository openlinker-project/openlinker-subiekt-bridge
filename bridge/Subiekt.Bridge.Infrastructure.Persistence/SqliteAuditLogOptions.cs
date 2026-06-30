namespace Subiekt.Bridge.Infrastructure.Persistence;

/// <summary>
/// Configuration for <see cref="SqliteAuditLog"/>. Bound from config in the
/// composition root (Faza 3B). <see cref="DatabasePath"/> defaults to
/// <c>audit-log.db</c> in the current directory; point it at a writable location
/// outside the source tree.
/// </summary>
public sealed class SqliteAuditLogOptions
{
    /// <summary>Path to the SQLite database file. Parent directory is created if missing.</summary>
    public string DatabasePath { get; set; } = "audit-log.db";
}
