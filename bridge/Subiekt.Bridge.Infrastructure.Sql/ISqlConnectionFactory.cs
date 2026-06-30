using System.Data.Common;

namespace Subiekt.Bridge.Infrastructure.Sql;

/// <summary>
/// Creates fresh, read-only SQL connections from <see cref="SqlReadOptions"/>.
/// The connection is returned unopened; callers (or Dapper) open it.
/// </summary>
public interface ISqlConnectionFactory
{
    /// <summary>A new, unopened connection to the read database.</summary>
    DbConnection Create();
}
