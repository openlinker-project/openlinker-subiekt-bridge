using Dapper;
using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Infrastructure.Sql;

/// <summary>
/// Dapper-backed <see cref="ICustomerQuery"/>. Ports the legacy
/// <c>/api/customers</c>, <c>/api/customers/{id}</c> and the NIP lookup into
/// a typed <see cref="CustomerView"/>.
/// </summary>
public sealed class SqlCustomerQuery : ICustomerQuery
{
    private const string ListSql = @"
        SELECT TOP (@limit)
               Id, NazwaSkrocona,
               NIP AS Nip, NIPSformatowany AS NipSformatowany,
               Telefon, Sygnatura_PelnaSygnatura AS Sygnatura,
               Aktywny, Kontrahent
        FROM ModelDanychContainer.Podmioty
        WHERE Kontrahent = 1
        ORDER BY NazwaSkrocona;";

    private const string GetByIdSql = @"
        SELECT Id, NazwaSkrocona,
               NIP AS Nip, NIPSformatowany AS NipSformatowany,
               Telefon, Sygnatura_PelnaSygnatura AS Sygnatura,
               Aktywny, Kontrahent
        FROM ModelDanychContainer.Podmioty
        WHERE Id = @id;";

    // Match on the canonical (digits-only) NIP column. Kontrahent = 1 keeps the
    // lookup to customers, mirroring the list query's scope.
    private const string FindByNipSql = @"
        SELECT TOP 1
               Id, NazwaSkrocona,
               NIP AS Nip, NIPSformatowany AS NipSformatowany,
               Telefon, Sygnatura_PelnaSygnatura AS Sygnatura,
               Aktywny, Kontrahent
        FROM ModelDanychContainer.Podmioty
        WHERE NIP = @nip AND Kontrahent = 1
        ORDER BY Id;";

    private readonly ISqlConnectionFactory _factory;
    private readonly int _maxLimit;

    public SqlCustomerQuery(ISqlConnectionFactory factory, SqlReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _maxLimit = options.MaxLimit;
    }

    public async Task<Result<IReadOnlyList<CustomerView>>> ListAsync(int limit, CancellationToken cancellationToken = default)
    {
        var capped = LimitGuard.Clamp(limit, _maxLimit, 50);
        try
        {
            await using var conn = _factory.Create();
            var rows = await conn.QueryAsync<CustomerView>(
                new CommandDefinition(ListSql, new { limit = capped }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return Result.Success<IReadOnlyList<CustomerView>>(rows.AsList());
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<CustomerView>>(
                new Error(SqlErrorClassifier.Classify(ex), "Nie udało się odczytać listy kontrahentów."));
        }
    }

    public async Task<Result<CustomerView?>> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = _factory.Create();
            var row = await conn.QueryFirstOrDefaultAsync<CustomerView>(
                new CommandDefinition(GetByIdSql, new { id }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return Result.Success<CustomerView?>(row);
        }
        catch (Exception ex)
        {
            return Result.Failure<CustomerView?>(
                new Error(SqlErrorClassifier.Classify(ex), "Nie udało się odczytać kontrahenta."));
        }
    }

    public async Task<Result<CustomerView?>> FindByNipAsync(string nip, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = _factory.Create();
            var row = await conn.QueryFirstOrDefaultAsync<CustomerView>(
                new CommandDefinition(FindByNipSql, new { nip }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return Result.Success<CustomerView?>(row);
        }
        catch (Exception ex)
        {
            return Result.Failure<CustomerView?>(
                new Error(SqlErrorClassifier.Classify(ex), "Nie udało się odczytać kontrahenta po NIP."));
        }
    }
}
