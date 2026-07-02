using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Application.Ports;

/// <summary>
/// A branch (Oddzial / JednostkaOrganizacyjna) row, as configured under Subiekt's
/// organizational-unit hierarchy (issue #5). Independent of the seller Podmiot axis
/// from issue #3 - a single-payer install can still have multiple branches.
/// </summary>
public sealed record BranchView(int Id, string? Nazwa);

/// <summary>
/// Read-only port over the seller's branches (issue #5). Implemented by the SQL
/// adapter against a non-Sfera-locked connection (3A read pattern), mirroring
/// <see cref="IBankAccountsReader"/>.
/// </summary>
public interface IBranchesReader
{
    /// <summary>List every Oddzial (JednostkiOrganizacyjne_Oddzial row).</summary>
    Task<Result<IReadOnlyList<BranchView>>> ListAsync(CancellationToken cancellationToken = default);
}
