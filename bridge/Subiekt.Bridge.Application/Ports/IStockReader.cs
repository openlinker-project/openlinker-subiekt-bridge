using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Application.Ports;

/// <summary>A warehouse (magazyn) row.</summary>
public sealed record WarehouseView(int Id, string Symbol, string? Nazwa, string? Opis);

/// <summary>
/// A stock level (stan magazynowy) for a product in a warehouse, including the
/// reservation breakdown the legacy <c>/api/stock</c> endpoints exposed.
/// </summary>
public sealed record StockLevel(
    string MagazynSymbol,
    string? MagazynNazwa,
    string? TowarSymbol,
    string? TowarNazwa,
    decimal IloscDostepna,
    decimal IloscZarezerwowanaIlosciowo,
    decimal IloscZarezerwowanaDostawowo,
    decimal IloscZadysponowana,
    int AsortymentId,
    int MagazynId);

/// <summary>A delivery batch (partia) for a product, as read by <c>/api/batches</c>.</summary>
public sealed record BatchView(
    int PartiaId,
    string? NumerPartii,
    decimal Ilosc,
    DateTime? Termin,
    string? TowarSymbol,
    string? TowarNazwa,
    int AsortymentId);

/// <summary>
/// Read-only port over warehouse stock. Implemented by the SQL adapter against a
/// non-Sfera-locked connection.
/// </summary>
public interface IStockReader
{
    /// <summary>List warehouses, ordered by symbol.</summary>
    Task<Result<IReadOnlyList<WarehouseView>>> ListWarehousesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// List stock levels, optionally filtered by warehouse symbol and/or product
    /// symbol, capped at <paramref name="limit"/>.
    /// </summary>
    Task<Result<IReadOnlyList<StockLevel>>> ReadStockAsync(
        string? magazyn,
        string? symbol,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>List stock levels for a single product across all warehouses.</summary>
    Task<Result<IReadOnlyList<StockLevel>>> ReadStockBySymbolAsync(string symbol, CancellationToken cancellationToken = default);

    /// <summary>List the in-stock delivery batches for a single product.</summary>
    Task<Result<IReadOnlyList<BatchView>>> ReadBatchesAsync(string symbol, CancellationToken cancellationToken = default);
}
