using Dapper;
using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Infrastructure.Sql;

/// <summary>
/// Dapper-backed <see cref="IStockReader"/>. Ports the legacy <c>/api/warehouses</c>,
/// <c>/api/stock</c>, <c>/api/stock/{symbol}</c> and <c>/api/batches/{symbol}</c>
/// SQL into typed read-models.
/// </summary>
public sealed class SqlStockReader : IStockReader
{
    private const string WarehousesSql = @"
        SELECT Id, Symbol, Nazwa, Opis
        FROM ModelDanychContainer.Magazyny
        ORDER BY Symbol;";

    // Optional filters preserved exactly as the legacy endpoint: @magazyn / @symbol
    // are NULL when not supplied, so the predicate short-circuits.
    private const string StockSql = @"
        SELECT TOP (@limit)
            m.Symbol AS MagazynSymbol, m.Nazwa AS MagazynNazwa,
            a.Symbol AS TowarSymbol,   a.Nazwa AS TowarNazwa,
            sm.IloscDostepna,
            sm.IloscZarezerwowanaIlosciowo,
            sm.IloscZarezerwowanaDostawowo,
            sm.IloscZadysponowana,
            sm.Asortyment_Id AS AsortymentId, sm.Magazyn_Id AS MagazynId
        FROM ModelDanychContainer.StanyMagazynowe sm
        JOIN ModelDanychContainer.Magazyny    m ON m.Id = sm.Magazyn_Id
        JOIN ModelDanychContainer.Asortymenty a ON a.Id = sm.Asortyment_Id
        WHERE (@magazyn IS NULL OR m.Symbol = @magazyn)
          AND (@symbol  IS NULL OR a.Symbol = @symbol)
        ORDER BY a.Symbol, m.Symbol;";

    private const string StockBySymbolSql = @"
        SELECT m.Symbol AS MagazynSymbol, m.Nazwa AS MagazynNazwa,
               a.Symbol AS TowarSymbol,   a.Nazwa AS TowarNazwa,
               sm.IloscDostepna,
               sm.IloscZarezerwowanaIlosciowo,
               sm.IloscZarezerwowanaDostawowo,
               sm.IloscZadysponowana
        FROM ModelDanychContainer.StanyMagazynowe sm
        JOIN ModelDanychContainer.Magazyny    m ON m.Id = sm.Magazyn_Id
        JOIN ModelDanychContainer.Asortymenty a ON a.Id = sm.Asortyment_Id
        WHERE a.Symbol = @symbol
        ORDER BY m.Symbol;";

    private const string BatchesSql = @"
        SELECT p.Id     AS PartiaId,
               p.Numer  AS NumerPartii,
               p.Ilosc,
               p.Termin,
               a.Symbol AS TowarSymbol,
               a.Nazwa  AS TowarNazwa,
               prz.Asortyment_Id AS AsortymentId
        FROM ModelDanychContainer.Partie p
        JOIN ModelDanychContainer.Przyjecia  prz ON prz.Id = p.Przyjecie_Id
        JOIN ModelDanychContainer.Asortymenty a  ON a.Id = prz.Asortyment_Id
        WHERE a.Symbol = @symbol
          AND p.Ilosc > 0
        ORDER BY p.Id ASC;";

    private readonly ISqlConnectionFactory _factory;
    private readonly int _maxLimit;

    public SqlStockReader(ISqlConnectionFactory factory, SqlReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _maxLimit = options.MaxLimit;
    }

    public async Task<Result<IReadOnlyList<WarehouseView>>> ListWarehousesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = _factory.Create();
            var rows = await conn.QueryAsync<WarehouseView>(
                new CommandDefinition(WarehousesSql, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return Result.Success<IReadOnlyList<WarehouseView>>(rows.AsList());
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<WarehouseView>>(
                new Error(SqlErrorClassifier.Classify(ex), "Nie udało się odczytać listy magazynów."));
        }
    }

    public async Task<Result<IReadOnlyList<StockLevel>>> ReadStockAsync(
        string? magazyn,
        string? symbol,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var capped = LimitGuard.Clamp(limit, _maxLimit, 200);
        try
        {
            await using var conn = _factory.Create();
            var rows = await conn.QueryAsync<StockLevel>(
                new CommandDefinition(
                    StockSql,
                    new { limit = capped, magazyn, symbol },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return Result.Success<IReadOnlyList<StockLevel>>(rows.AsList());
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<StockLevel>>(
                new Error(SqlErrorClassifier.Classify(ex), "Nie udało się odczytać stanów magazynowych."));
        }
    }

    public async Task<Result<IReadOnlyList<StockLevel>>> ReadStockBySymbolAsync(string symbol, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = _factory.Create();
            var rows = await conn.QueryAsync<StockLevel>(
                new CommandDefinition(StockBySymbolSql, new { symbol }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return Result.Success<IReadOnlyList<StockLevel>>(rows.AsList());
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<StockLevel>>(
                new Error(SqlErrorClassifier.Classify(ex), "Nie udało się odczytać stanów magazynowych dla towaru."));
        }
    }

    public async Task<Result<IReadOnlyList<BatchView>>> ReadBatchesAsync(string symbol, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = _factory.Create();
            var rows = await conn.QueryAsync<BatchView>(
                new CommandDefinition(BatchesSql, new { symbol }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return Result.Success<IReadOnlyList<BatchView>>(rows.AsList());
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<BatchView>>(
                new Error(SqlErrorClassifier.Classify(ex), "Nie udało się odczytać partii towaru."));
        }
    }
}
