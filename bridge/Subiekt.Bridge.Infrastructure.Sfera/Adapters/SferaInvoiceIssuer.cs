using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;
using Subiekt.Bridge.Domain.Invoices;

namespace Subiekt.Bridge.Infrastructure.Sfera.Adapters;

/// <summary>
/// <see cref="IInvoiceIssuer"/> implemented over the moved
/// <see cref="SferaDokumentySprzedazyService"/>, routed through
/// <see cref="SferaWriteQueue"/>.
/// <para>
/// Domain rules ARE used for discount folding: the document is passed through
/// <see cref="SalesDocument.FoldDiscounts"/> first, so the lines handed to Sfera carry
/// no negative (discount) positions and their gross prices already reflect the
/// proportional discount. The legacy service's inline <c>rabatFactor</c> then becomes a
/// no-op (no negative lines remain), preserving identical totals via the single,
/// unit-tested Domain rule instead of the duplicated inline math.
/// </para>
/// <para>
/// Fiscal dates are computed by the Domain aggregate via
/// <see cref="SalesDocument.ComputeFiscalDates"/> (sale/VAT date = the document issue date,
/// dispatch/entry date = the injected <see cref="IClock"/> now) and passed to the service as
/// resolved values, so the Sfera service no longer derives dates inline.
/// </para>
/// </summary>
public sealed class SferaInvoiceIssuer : IInvoiceIssuer
{
    private readonly SferaWriteQueue _queue;
    private readonly SferaDokumentySprzedazyService _dokumenty;
    private readonly IClock _clock;

    public SferaInvoiceIssuer(SferaWriteQueue queue, SferaDokumentySprzedazyService dokumenty, IClock clock)
    {
        _queue = queue;
        _dokumenty = dokumenty;
        _clock = clock;
    }

    public async Task<Result<DocumentRef>> IssueAsync(SalesDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var input = ToInput(document, _clock);
        var isParagon = document.DocumentType == DocumentType.PA;

        try
        {
            var (id, numer) = await _queue.EnqueueAsync(session =>
                isParagon
                    ? _dokumenty.UtworzParagon(session, input)
                    : _dokumenty.UtworzFaktura(session, input),
                cancellationToken).ConfigureAwait(false);

            return Result.Success(new DocumentRef(id, numer));
        }
        catch (Exception ex)
        {
            var bex = BridgeException.Classify(ex);
            return Result.Failure<DocumentRef>(new Error(bex.CodeString, bex.Reason));
        }
    }

    // Apply the Domain discount-folding rule and fiscal-date computation, then translate the
    // folded lines into the Infrastructure-local invoice input. Both fiscal dates come from
    // the Domain aggregate (ComputeFiscalDates), so the Sfera service sets them verbatim.
    internal static SferaInvoiceInput ToInput(SalesDocument document, IClock clock)
    {
        var folded = document.FoldDiscounts();
        var fiscal = document.ComputeFiscalDates(clock);

        var lines = folded.Lines
            .Select(l => new SferaInvoiceLineInput(
                TowarSymbol: l.ProductSymbol,
                Ilosc: l.Quantity,
                CenaBrutto: l.UnitGrossPrice.Amount,
                StawkaVAT: l.VatRate.Symbol,
                Name: l.OneTimeName))
            .ToList();

        // Explicit payment selection (issue #1) rides along; the service applies it
        // in step 6b instead of the config-driven defaults.
        var payment = document.Payment is { } p
            ? new SferaPaymentInput(
                Method: p.Method == PaymentMethod.Cash ? "cash" : "transfer",
                BankAccountId: p.BankAccountId,
                Currency: document.Currency)
            : null;

        // Explicit Stanowisko Kasowe selection (issue #5) rides along the same way.
        var stanowiskoKasoweId = document.CashRegister?.StanowiskoKasoweId;

        return new SferaInvoiceInput(
            KontrahentId: document.BuyerId,
            DataSprzedazy: fiscal.DataSprzedazy.LocalDateTime,
            DataWydania: fiscal.DataWydania.LocalDateTime,
            Lines: lines,
            Payment: payment,
            StanowiskoKasoweId: stanowiskoKasoweId);
    }
}
