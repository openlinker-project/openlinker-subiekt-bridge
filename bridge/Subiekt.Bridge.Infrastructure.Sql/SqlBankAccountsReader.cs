using Dapper;
using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Infrastructure.Sql;

/// <summary>
/// Dapper-backed <see cref="IBankAccountsReader"/> (issue #1). Schema facts
/// verified live (see <c>docs/spikes/bank-account-probe-findings.md</c> s.1):
/// <list type="bullet">
/// <item><c>RachunekBankowy</c> is a TPT subtype — number/flags live in
/// <c>CentraGromadzeniaFinansow_RachunekBankowy</c>, the display name on the
/// <c>CentraGromadzeniaFinansow</c> base row.</item>
/// <item>Seller scoping: <c>Wlasciciel_Id</c> = a MojaFirma Podmiot
/// (<c>Typ = 2 AND Podtyp = 11</c>); the table also holds ZUS/US and client
/// accounts, so the filter is mandatory. An install may have MORE THAN ONE
/// such Podmiot (multiple payers/branches) — issue #3 — so every matching
/// Podmiot is enumerated, not just one.</item>
/// <item><c>Podmioty</c> has NO <c>Nazwa</c> column — verified live against
/// <c>Nexo_Demo_1</c> (issue #3/#5 Phase 1 probe, 2026-07-02): querying
/// <c>owner.Nazwa</c> throws <c>Invalid column name 'Nazwa'</c> (128-column
/// schema dump confirms the display-name column is <c>NazwaSkrocona</c>).
/// This was an unverified assumption at PR #4 authoring time; caught by the
/// first live run.</item>
/// <item>The UI's "Podstawowy" flag is <c>WlascicielPodstawowego_Id IS NOT NULL</c>
/// (back-reference from <c>Podmiot.RachunekPodstawowy</c>), NOT
/// <c>PodstawowyDlaWaluty</c>.</item>
/// </list>
/// </summary>
public sealed class SqlBankAccountsReader : IBankAccountsReader
{
    private const string ListSql = @"
        SELECT rb.Id,
               cgf.Nazwa,
               rb.Numer,
               rb.NumerBanku,
               rb.Opis,
               w.Symbol AS Waluta,
               rb.JestRachunkiemVAT,
               CAST(CASE WHEN rb.WlascicielPodstawowego_Id IS NOT NULL THEN 1 ELSE 0 END AS bit) AS IsDefault,
               rb.Wlasciciel_Id AS OwnerPodmiotId,
               owner.NazwaSkrocona AS OwnerName
        FROM ModelDanychContainer.CentraGromadzeniaFinansow_RachunekBankowy rb
        JOIN ModelDanychContainer.CentraGromadzeniaFinansow cgf ON cgf.Id = rb.Id
        LEFT JOIN ModelDanychContainer.Waluty w ON w.Id = rb.Waluta_Id
        JOIN ModelDanychContainer.Podmioty owner ON owner.Id = rb.Wlasciciel_Id
        WHERE rb.Aktywny = 1
          AND owner.Typ = 2 AND owner.Podtyp = 11
        ORDER BY rb.Wlasciciel_Id, IsDefault DESC, rb.Id;";

    private readonly ISqlConnectionFactory _factory;

    public SqlBankAccountsReader(ISqlConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task<Result<IReadOnlyList<BankAccountView>>> ListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = _factory.Create();
            var rows = await conn.QueryAsync<BankAccountView>(
                new CommandDefinition(ListSql, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return Result.Success<IReadOnlyList<BankAccountView>>(rows.AsList());
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<BankAccountView>>(
                new Error(SqlErrorClassifier.Classify(ex), "Nie udało się odczytać listy rachunków bankowych."));
        }
    }
}
