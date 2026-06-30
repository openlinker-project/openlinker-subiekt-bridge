using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Application.Ports;

/// <summary>
/// A product (asortyment) row as read from the provider database.
/// Mirrors the columns the legacy <c>/api/products</c> endpoints returned.
/// </summary>
public sealed record ProductView(
    int Id,
    string Symbol,
    string? Nazwa,
    string? Opis,
    decimal? CenaEwidencyjna,
    string? Pkwiu,
    string? KodCn,
    int? Numer,
    int? RodzajId);

/// <summary>
/// Read-only port over the product catalogue. The SQL adapter implements this
/// against the provider's read replica / non-locked connection so listing
/// products does not contend with the Sfera write session.
/// </summary>
public interface IProductCatalogReader
{
    /// <summary>List products, capped at <paramref name="limit"/>, excluding packaging.</summary>
    Task<Result<IReadOnlyList<ProductView>>> ListAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Get a single product by its symbol; null value when not found.</summary>
    Task<Result<ProductView?>> GetBySymbolAsync(string symbol, CancellationToken cancellationToken = default);
}
