using Subiekt.Bridge.Domain.Common;
using Subiekt.Bridge.Domain.Customers;

namespace Subiekt.Bridge.Application.Ports;

/// <summary>
/// The provider-side result of a customer upsert: the assigned id and full number
/// (sygnatura). Mirrors the legacy <c>(int Id, string Numer)</c> tuple.
/// </summary>
public readonly record struct CustomerRef(int Id, string Numer);

/// <summary>
/// Port for reading and writing customers (kontrahenci) in the provider (Subiekt).
/// The driving adapter (Faza 3: Infrastructure.Sfera; transitional: the legacy
/// <c>IPodmiotyService</c> adapter in Api) implements this.
/// </summary>
public interface ICustomerDirectory
{
    /// <summary>
    /// Create-or-return a customer. Implementations are expected to be idempotent
    /// by NIP (an existing kontrahent with the same NIP is returned, not duplicated).
    /// </summary>
    Task<Result<CustomerRef>> UpsertAsync(Customer customer, CancellationToken cancellationToken = default);

    /// <summary>Find a customer by its validated NIP; null result value when none exists.</summary>
    Task<Result<CustomerRef?>> FindByNipAsync(Nip nip, CancellationToken cancellationToken = default);
}
