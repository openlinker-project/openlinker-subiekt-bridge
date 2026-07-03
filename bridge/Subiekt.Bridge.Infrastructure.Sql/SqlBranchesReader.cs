using Dapper;
using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Infrastructure.Sql;

/// <summary>
/// Dapper-backed <see cref="IBranchesReader"/> (issue #5). Schema facts verified live
/// against <c>Nexo_Demo_1</c> (see docs/spikes/podmioty-oddzial-stanowisko-probe-findings.md
/// s.2): Oddzialy live in <c>ModelDanychContainer.JednostkiOrganizacyjne_Oddzial</c>
/// (Id, Nazwa directly on the table - no TPT base-row join needed, unlike
/// <c>RachunekBankowy</c>), keyed to a shared <c>Centrala_Id</c> head-office unit
/// (not itself an "Oddzial" row).
/// </summary>
public sealed class SqlBranchesReader : IBranchesReader
{
    private const string ListSql = @"
        SELECT Id, Nazwa
        FROM ModelDanychContainer.JednostkiOrganizacyjne_Oddzial
        ORDER BY Id;";

    private readonly ISqlConnectionFactory _factory;

    public SqlBranchesReader(ISqlConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task<Result<IReadOnlyList<BranchView>>> ListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = _factory.Create();
            var rows = await conn.QueryAsync<BranchView>(
                new CommandDefinition(ListSql, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return Result.Success<IReadOnlyList<BranchView>>(rows.AsList());
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<BranchView>>(
                new Error(SqlErrorClassifier.Classify(ex), "Nie udało się odczytać listy oddziałów."));
        }
    }
}
