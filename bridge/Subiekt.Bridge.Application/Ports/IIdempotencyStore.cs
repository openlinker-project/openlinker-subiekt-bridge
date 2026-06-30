using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Application.Ports;

/// <summary>
/// A previously issued provider invoice remembered against an idempotency key:
/// the provider invoice id plus its full number (sygnatura). Mirrors the legacy
/// file-backed map entry <c>{ Id, Numer }</c>.
/// </summary>
public readonly record struct IdempotentInvoice(int ProviderInvoiceId, string? ProviderInvoiceNumber);

/// <summary>
/// Port for idempotency: remember the result of an issue-invoice operation keyed
/// by an idempotency key so a retried call returns the SAME provider invoice
/// instead of duplicating a (permanent) fiscal document.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Returns the previously stored invoice for <paramref name="key"/>, or a
    /// null value when the key is unknown. A failed <see cref="Result"/> signals
    /// an infrastructure fault (not a cache miss).
    /// </summary>
    Task<Result<IdempotentInvoice?>> TryGetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Durably remember that <paramref name="key"/> produced
    /// <paramref name="invoice"/>. Implementations must persist atomically so a
    /// crash mid-write cannot corrupt or lose the map.
    /// </summary>
    Task<Result> StoreAsync(string key, IdempotentInvoice invoice, CancellationToken cancellationToken = default);
}
