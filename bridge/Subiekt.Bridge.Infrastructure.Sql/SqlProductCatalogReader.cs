using Dapper;
using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Infrastructure.Sql;

/// <summary>
/// Dapper-backed <see cref="IProductCatalogReader"/>. Ports the legacy
/// <c>/api/products</c> and <c>/api/products/{symbol}</c> SQL, returning a typed
/// <see cref="ProductView"/> instead of a <c>Dictionary&lt;string,object?&gt;</c>.
/// </summary>
public sealed class SqlProductCatalogReader : IProductCatalogReader
{
    // Rodzaj_Id: 2 = towar, 1 = usługa, 4 = komplet, 3 = opakowanie.
    // Packaging (3) is excluded — it can't be sold as a normal invoice line
    // (preserves the legacy WHERE Rodzaj_Id <> 3).
    private const string ListSql = @"
        SELECT TOP (@limit)
               Id, Symbol, Nazwa, Opis, CenaEwidencyjna,
               PKWiU AS Pkwiu, KodCN AS KodCn, Numer, Rodzaj_Id AS RodzajId
        FROM ModelDanychContainer.Asortymenty
        WHERE IsInRecycleBin = 0 AND Rodzaj_Id <> 3
        ORDER BY Symbol;";

    private const string GetBySymbolSql = @"
        SELECT Id, Symbol, Nazwa, Opis, CenaEwidencyjna,
               PKWiU AS Pkwiu, KodCN AS KodCn, Numer, NULL AS RodzajId
        FROM ModelDanychContainer.Asortymenty
        WHERE Symbol = @symbol AND IsInRecycleBin = 0;";

    private readonly ISqlConnectionFactory _factory;
    private readonly int _maxLimit;

    public SqlProductCatalogReader(ISqlConnectionFactory factory, SqlReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _maxLimit = options.MaxLimit;
    }

    public async Task<Result<IReadOnlyList<ProductView>>> ListAsync(int limit, CancellationToken cancellationToken = default)
    {
        var capped = LimitGuard.Clamp(limit, _maxLimit, 50);
        try
        {
            await using var conn = _factory.Create();
            var rows = await conn.QueryAsync<ProductView>(
                new CommandDefinition(ListSql, new { limit = capped }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return Result.Success<IReadOnlyList<ProductView>>(rows.AsList());
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<ProductView>>(
                new Error(SqlErrorClassifier.Classify(ex), "Nie udało się odczytać listy towarów."));
        }
    }

    public async Task<Result<ProductView?>> GetBySymbolAsync(string symbol, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = _factory.Create();
            var row = await conn.QueryFirstOrDefaultAsync<ProductView>(
                new CommandDefinition(GetBySymbolSql, new { symbol }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return Result.Success<ProductView?>(row);
        }
        catch (Exception ex)
        {
            return Result.Failure<ProductView?>(
                new Error(SqlErrorClassifier.Classify(ex), "Nie udało się odczytać towaru."));
        }
    }
}
