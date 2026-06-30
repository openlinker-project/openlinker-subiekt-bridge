using Microsoft.Extensions.Logging;
using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;
using Subiekt.Bridge.Domain.Invoices;

namespace Subiekt.Bridge.Infrastructure.Sfera.Adapters;

/// <summary>
/// <see cref="IIssueInvoiceWithBuyer"/> — runs the optional buyer upsert and the
/// invoice issue as ONE work item on <see cref="SferaWriteQueue"/>, so the two-step
/// "self-sufficient" <c>POST /api/invoices</c> flow is contained in a single serialized
/// unit instead of two independently-queued operations.
/// <para>
/// Compensation: a Subiekt podmiot, once saved, cannot generally be rolled back (it is
/// persistent master data, like the fiscal document). If the buyer is created and the
/// invoice then fails, we log a clear compensation note carrying the orphaned buyer id
/// so an operator can reconcile; we do NOT attempt to delete the podmiot.
/// </para>
/// </summary>
public sealed class SferaIssueInvoiceWithBuyer : IIssueInvoiceWithBuyer
{
    private readonly SferaWriteQueue _queue;
    private readonly SferaPodmiotyService _podmioty;
    private readonly SferaDokumentySprzedazyService _dokumenty;
    private readonly IClock _clock;
    private readonly ILogger<SferaIssueInvoiceWithBuyer> _log;

    public SferaIssueInvoiceWithBuyer(
        SferaWriteQueue queue,
        SferaPodmiotyService podmioty,
        SferaDokumentySprzedazyService dokumenty,
        IClock clock,
        ILogger<SferaIssueInvoiceWithBuyer> log)
    {
        _queue = queue;
        _podmioty = podmioty;
        _dokumenty = dokumenty;
        _clock = clock;
        _log = log;
    }

    public async Task<Result<DocumentRef>> IssueAsync(
        SalesDocument document,
        InlineBuyer? buyer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var isParagon = document.DocumentType == DocumentType.PA;
        // Pre-fold discounts via the Domain rule, identical to SferaInvoiceIssuer.
        var invoiceInputTemplate = SferaInvoiceIssuer.ToInput(document, _clock);
        var buyerInput = buyer.HasValue ? SferaCustomerDirectory.ToInput(buyer.Value.Customer) : null;
        var isCompany = buyer?.Customer.IsCompany ?? true;

        try
        {
            var (id, numer) = await _queue.EnqueueAsync(session =>
            {
                // Single serialized unit: upsert buyer (if inline) then issue.
                var input = invoiceInputTemplate;
                if (buyerInput is not null)
                {
                    var (buyerId, _) = isCompany
                        ? _podmioty.UpsertFirma(session, buyerInput)
                        : _podmioty.UpsertOsoba(session, buyerInput);

                    input = input with { KontrahentId = buyerId };

                    try
                    {
                        return isParagon
                            ? _dokumenty.UtworzParagon(session, input)
                            : _dokumenty.UtworzFaktura(session, input);
                    }
                    catch (Exception)
                    {
                        // Buyer was created but the invoice failed in the same unit.
                        // A saved podmiot can't be rolled back — log a compensation note.
                        _log.LogWarning(
                            "COMPENSATION: invoice issue failed after buyer upsert in the same write unit. " +
                            "Orphaned/created kontrahent id={buyerId} remains in Subiekt (persistent master data — " +
                            "no automatic rollback). Reconcile manually if this buyer was newly created.",
                            buyerId);
                        throw;
                    }
                }

                return isParagon
                    ? _dokumenty.UtworzParagon(session, input)
                    : _dokumenty.UtworzFaktura(session, input);
            }, cancellationToken).ConfigureAwait(false);

            return Result.Success(new DocumentRef(id, numer));
        }
        catch (Exception ex)
        {
            var bex = BridgeException.Classify(ex);
            return Result.Failure<DocumentRef>(new Error(bex.CodeString, bex.Reason));
        }
    }
}
