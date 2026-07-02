using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Application.Ports;

/// <summary>
/// A cash-register station (Stanowisko Kasowe) row (issue #5). <c>Id</c> is the value to
/// pass as <c>stanowiskoKasoweId</c> on <c>POST /api/invoices</c>. <see cref="OddzialId"/>
/// is the branch it happens to be linked to via <c>StanowiskoKasoweJednostkaOrganizacyjna</c>
/// (null = unlinked) - INFORMATIONAL ONLY. Per-invoice branch (Oddzial) selection is not
/// supported at all (docs/spikes/podmioty-oddzial-stanowisko-probe-findings.md s.8: a
/// document's Oddzial comes from the session's read-only business context, not a
/// per-document field), so this link does not gate which stations are usable.
/// </summary>
public sealed record CashRegisterView(int Id, string? Nazwa, string? Symbol, int? OddzialId);

/// <summary>
/// Read-only port over the seller's cash-register stations (issue #5). Implemented by the
/// SQL adapter against a non-Sfera-locked connection (3A read pattern), mirroring
/// <see cref="IBankAccountsReader"/>.
/// </summary>
public interface ICashRegistersReader
{
    /// <summary>List every Stanowisko Kasowe with its Oddzial link (if any).</summary>
    Task<Result<IReadOnlyList<CashRegisterView>>> ListAsync(CancellationToken cancellationToken = default);
}
