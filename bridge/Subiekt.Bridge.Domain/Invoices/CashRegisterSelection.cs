using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Domain.Invoices;

/// <summary>
/// An explicit Stanowisko Kasowe (cash-register station) selection for a sales document
/// (issue #5). Originally scoped to also select an Oddzial (branch) - dropped after live
/// investigation (docs/spikes/podmioty-oddzial-stanowisko-probe-findings.md s.8) proved
/// branch routing is not achievable per-document: a Subiekt document's operative Oddzial
/// comes from the LOGGED-IN SESSION's <c>IKontekstBiznesowy</c> (a read-only, session-bound
/// business context - Oddzial, Magazyn, StanowiskoKasowe, Podmiot, RachunekBankowy all
/// fixed to whichever workplace the authenticated user is configured for), never from a
/// per-document field. Neither patching <c>Dane.MiejsceWprowadzenia</c> after creation nor
/// passing an Oddzial via <c>ParametryTworzeniaDokumentu</c> at creation time overrides it -
/// both were tried live and both failed identically. Routing to a non-default branch would
/// require the bridge to authenticate as a different Subiekt user per branch, which is a
/// session-architecture change out of scope for a per-invoice selector.
/// </summary>
public sealed class CashRegisterSelection
{
    private CashRegisterSelection(int stanowiskoKasoweId)
    {
        StanowiskoKasoweId = stanowiskoKasoweId;
    }

    /// <summary>Subiekt <c>CentraGromadzeniaFinansow_StanowiskoKasowe</c> id.</summary>
    public int StanowiskoKasoweId { get; }

    /// <summary>
    /// Parse the wire value into a selection. Success with <c>null</c> means "no
    /// selection" (field absent) - the caller keeps the implicit-default station.
    /// </summary>
    public static Result<CashRegisterSelection?> TryCreate(int? stanowiskoKasoweId)
    {
        if (stanowiskoKasoweId is null)
            return Result.Success<CashRegisterSelection?>(null);

        if (stanowiskoKasoweId is not > 0)
            return Result.Failure<CashRegisterSelection?>(new Error(
                "cashRegister.stanowisko",
                "stanowiskoKasoweId must be a positive id."));

        return Result.Success<CashRegisterSelection?>(new CashRegisterSelection(stanowiskoKasoweId.Value));
    }
}
