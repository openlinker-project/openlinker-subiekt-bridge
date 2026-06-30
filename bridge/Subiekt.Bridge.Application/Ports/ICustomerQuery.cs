using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Application.Ports;

/// <summary>
/// A customer (kontrahent) row as read from the provider database. Mirrors the
/// columns the legacy <c>/api/customers</c> endpoints returned.
/// </summary>
public sealed record CustomerView(
    int Id,
    string? NazwaSkrocona,
    string? Nip,
    string? NipSformatowany,
    string? Telefon,
    string? Sygnatura,
    bool? Aktywny,
    bool? Kontrahent);

/// <summary>
/// Read-only port over the customer directory. Distinct from
/// <see cref="ICustomerDirectory"/> (the write/upsert port implemented by Sfera):
/// this is implemented by the SQL adapter for non-locking lookups.
/// </summary>
public interface ICustomerQuery
{
    /// <summary>List customers (Kontrahent = 1), capped at <paramref name="limit"/>.</summary>
    Task<Result<IReadOnlyList<CustomerView>>> ListAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Get a single customer by id; null value when not found.</summary>
    Task<Result<CustomerView?>> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Find the first customer with the given (canonical, digits-only) NIP; null value when none.</summary>
    Task<Result<CustomerView?>> FindByNipAsync(string nip, CancellationToken cancellationToken = default);
}
