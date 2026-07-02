using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Application.Ports;

/// <summary>
/// A cash-register station (Stanowisko Kasowe) row (issue #5). <see cref="OddzialId"/> is
/// the branch it is explicitly restricted to via <c>StanowiskoKasoweJednostkaOrganizacyjna</c>
/// - null means unlinked. Live probing (docs/spikes/podmioty-oddzial-stanowisko-probe-findings.md
/// s.6-7) confirmed an unlinked station is reserved for the document's IMPLICIT-DEFAULT branch,
/// not usable from any explicit Oddzial - so a caller picking a non-default <c>oddzialId</c>
/// must choose a station with a matching <see cref="OddzialId"/>.
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
