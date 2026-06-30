using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Subiekt.Bridge.Infrastructure.Sql;

/// <summary>
/// Classifies an exception thrown by a SQL read-model into the OL contract's two
/// error classes by CODE (not by message round-tripping): infra/transient SQL
/// failures map to <c>"unreachable"</c> (503 — OL should retry), everything else
/// maps to <c>"rejected"</c> (422 — don't retry blindly).
/// </summary>
internal static class SqlErrorClassifier
{
    public const string Unreachable = "unreachable";
    public const string Rejected = "rejected";

    /// <summary>
    /// Returns <see cref="Unreachable"/> for connectivity/transient DB failures
    /// (<see cref="SqlException"/>, any ADO.NET <see cref="DbException"/>,
    /// <see cref="TimeoutException"/>); otherwise <see cref="Rejected"/>.
    /// </summary>
    public static string Classify(Exception ex)
        => ex is SqlException or DbException or TimeoutException ? Unreachable : Rejected;
}
