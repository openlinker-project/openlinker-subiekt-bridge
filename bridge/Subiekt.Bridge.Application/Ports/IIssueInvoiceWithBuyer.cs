using Subiekt.Bridge.Domain.Common;
using Subiekt.Bridge.Domain.Customers;
using Subiekt.Bridge.Domain.Invoices;

namespace Subiekt.Bridge.Application.Ports;

/// <summary>
/// Optional buyer details to upsert-then-bill in a single unit of work. When the
/// caller already resolved a provider buyer id (two-step flow), this is null and the
/// document's <see cref="SalesDocument.BuyerId"/> is used as-is.
/// </summary>
public readonly record struct InlineBuyer(Customer Customer);

/// <summary>
/// Issues a sales document together with an optional inline buyer upsert as ONE
/// atomic unit on the provider's single write worker. The legacy
/// <c>POST /api/invoices</c> auto-upserted the buyer then issued the invoice as two
/// separate queued operations; folding them into one work item contains a partial
/// failure (e.g. buyer created but invoice rejected) within a single serialized step
/// and lets the adapter emit a compensation note.
/// <para>
/// True rollback of a saved Subiekt podmiot is NOT generally possible (a kontrahent,
/// like a fiscal document, is persistent), so on invoice failure after a buyer was
/// created the implementation logs a compensation note rather than deleting the buyer.
/// </para>
/// </summary>
public interface IIssueInvoiceWithBuyer
{
    /// <summary>
    /// When <paramref name="buyer"/> is non-null, upsert the buyer first and bill the
    /// resulting provider id; otherwise bill <paramref name="document"/> as supplied.
    /// Both steps run inside one write-queue work item.
    /// </summary>
    Task<Result<DocumentRef>> IssueAsync(
        SalesDocument document,
        InlineBuyer? buyer,
        CancellationToken cancellationToken = default);
}
