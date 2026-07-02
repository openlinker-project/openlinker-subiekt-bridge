using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Domain.Invoices;

/// <summary>
/// An explicit Oddzial (branch) / Stanowisko Kasowe (cash-register station) selection
/// for a sales document (issue #5). Combination rules are strict, mirroring
/// <see cref="PaymentSelection"/>'s precedent:
/// <list type="bullet">
/// <item>both fields absent — no selection (today's implicit-default branch/station apply);</item>
/// <item><c>oddzialId</c> alone (no <c>stanowiskoKasoweId</c>) is REJECTED — live probing
/// (docs/spikes/podmioty-oddzial-stanowisko-probe-findings.md s.6) showed Sfera's implicit
/// default cash-register resolution does not scope to a non-default Oddzial, so a
/// cash/immediate-payment document issued under an explicit Oddzial without an explicit
/// station fails deep inside Sfera with no actionable message;</item>
/// <item><c>stanowiskoKasoweId</c> alone (no <c>oddzialId</c>) is ALLOWED — this picks a
/// specific cash-register while leaving the document's branch at its implicit default,
/// which live-probing confirmed works;</item>
/// <item>both present — allowed; cross-consistency (the station must be linked to the
/// given Oddzial, or unlinked stations are reserved for the implicit default branch) is
/// verified at issuance time (Infrastructure.Sfera), not here — same split as
/// <see cref="PaymentSelection.BankAccountId"/>'s ownership check.</item>
/// </list>
/// </summary>
public sealed class BranchSelection
{
    private BranchSelection(int? oddzialId, int stanowiskoKasoweId)
    {
        OddzialId = oddzialId;
        StanowiskoKasoweId = stanowiskoKasoweId;
    }

    /// <summary>Subiekt <c>JednostkaOrganizacyjna</c> (Oddzial) id. Null = document's implicit-default branch.</summary>
    public int? OddzialId { get; }

    /// <summary>Subiekt <c>CentraGromadzeniaFinansow_StanowiskoKasowe</c> id.</summary>
    public int StanowiskoKasoweId { get; }

    /// <summary>
    /// Parse the wire pair into a selection. Success with <c>null</c> means "no
    /// selection" (both fields absent) — the caller keeps the default behavior.
    /// </summary>
    public static Result<BranchSelection?> TryCreate(int? oddzialId, int? stanowiskoKasoweId)
    {
        if (oddzialId is null && stanowiskoKasoweId is null)
            return Result.Success<BranchSelection?>(null);

        if (oddzialId is not null && stanowiskoKasoweId is null)
            return Result.Failure<BranchSelection?>(new Error(
                "branch.stanowisko",
                "oddzialId requires an explicit stanowiskoKasoweId (Sfera's implicit default cash-register does not scope to a non-default branch)."));

        if (stanowiskoKasoweId is not > 0)
            return Result.Failure<BranchSelection?>(new Error(
                "branch.stanowisko",
                "stanowiskoKasoweId must be a positive id."));

        if (oddzialId is not null and not > 0)
            return Result.Failure<BranchSelection?>(new Error(
                "branch.oddzial",
                "oddzialId must be a positive id."));

        return Result.Success<BranchSelection?>(new BranchSelection(oddzialId, stanowiskoKasoweId.Value));
    }
}
