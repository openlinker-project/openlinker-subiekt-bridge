using Subiekt.Bridge.Domain.Common;
using Subiekt.Bridge.Domain.Invoices;

namespace Subiekt.Bridge.Application.Ports;

/// <summary>Provider-side result of issuing a document: id + full number (sygnatura).</summary>
public readonly record struct DocumentRef(int Id, string Numer);

/// <summary>
/// Port for issuing sales documents (faktura / paragon). Minimal for now;
/// fleshed out in Faza 3/4 when the real Sfera adapter lands.
/// </summary>
public interface IInvoiceIssuer
{
    Task<Result<DocumentRef>> IssueAsync(SalesDocument document, CancellationToken cancellationToken = default);
}
