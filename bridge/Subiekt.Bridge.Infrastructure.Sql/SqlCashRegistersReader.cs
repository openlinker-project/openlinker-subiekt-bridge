using Dapper;
using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Infrastructure.Sql;

/// <summary>
/// Dapper-backed <see cref="ICashRegistersReader"/> (issue #5). Schema facts verified live
/// against <c>Nexo_Demo_1</c> (see docs/spikes/podmioty-oddzial-stanowisko-probe-findings.md
/// s.3): <c>StanowiskoKasowe</c> is a <c>CentraGromadzeniaFinansow</c> TPT subtype (display
/// name on the base row, like <c>RachunekBankowy</c>); the Oddzial link is a many-to-many
/// junction table (<c>StanowiskoKasoweJednostkaOrganizacyjna</c>) even though a station is
/// only ever linked to zero or one Oddzial in practice - a correlated
/// <c>SELECT TOP 1 ... AS OddzialId</c> subquery per station keeps the query correct if that
/// ever changes.
/// </summary>
public sealed class SqlCashRegistersReader : ICashRegistersReader
{
    private const string ListSql = @"
        SELECT sk.Id,
               cgf.Nazwa,
               sk.Symbol,
               (SELECT TOP 1 link.JednostkiOrganizacyjne_Id
                FROM ModelDanychContainer.StanowiskoKasoweJednostkaOrganizacyjna link
                WHERE link.StanowiskaKasowe_Id = sk.Id) AS OddzialId
        FROM ModelDanychContainer.CentraGromadzeniaFinansow_StanowiskoKasowe sk
        JOIN ModelDanychContainer.CentraGromadzeniaFinansow cgf ON cgf.Id = sk.Id
        ORDER BY sk.Id;";

    private readonly ISqlConnectionFactory _factory;

    public SqlCashRegistersReader(ISqlConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task<Result<IReadOnlyList<CashRegisterView>>> ListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = _factory.Create();
            var rows = await conn.QueryAsync<CashRegisterView>(
                new CommandDefinition(ListSql, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return Result.Success<IReadOnlyList<CashRegisterView>>(rows.AsList());
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<CashRegisterView>>(
                new Error(SqlErrorClassifier.Classify(ex), "Nie udało się odczytać listy stanowisk kasowych."));
        }
    }
}
