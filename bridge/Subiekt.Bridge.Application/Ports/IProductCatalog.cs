using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Application.Ports;

/// <summary>Provider-side result of a product upsert.</summary>
public readonly record struct ProductRef(int Id, string Symbol, bool Created);

/// <summary>
/// Port for the product catalogue (asortymenty). Minimal interface stub;
/// fleshed out in a later phase.
/// </summary>
public interface IProductCatalog
{
    Task<Result<bool>> ExistsAsync(string symbol, CancellationToken cancellationToken = default);

    Task<Result<ProductRef>> UpsertAsync(
        string symbol,
        string nazwa,
        decimal cenaEwidencyjna,
        string? wzorzecSymbol = null,
        CancellationToken cancellationToken = default);
}
