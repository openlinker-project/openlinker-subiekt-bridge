using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Application.Ports;

/// <summary>
/// One persisted audit record. <see cref="TimestampUtc"/> is always UTC.
/// </summary>
public sealed record AuditEntry(
    long Id,
    DateTime TimestampUtc,
    string OperationType,
    string? InputJson,
    string? OutputJson,
    int StatusCode,
    string? ErrorMessage);

/// <summary>
/// Port for durable audit logging of bridge operations.
/// </summary>
public interface IAuditLog
{
    /// <summary>Append one audit record.</summary>
    Task<Result> LogAsync(
        string operationType,
        string? inputJson,
        string? outputJson,
        int statusCode,
        string? errorMessage = null,
        CancellationToken cancellationToken = default);

    /// <summary>Return the most recent <paramref name="limit"/> records, newest first.</summary>
    Task<Result<IReadOnlyList<AuditEntry>>> GetLastAsync(int limit = 10, CancellationToken cancellationToken = default);
}
