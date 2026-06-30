using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Application.Ports;

/// <summary>
/// Port for rendering an already-issued sales document (faktura / paragon) to its
/// printable PDF bytes. The render is headless (no GUI / print dialog) and MUST be
/// serialized against the live Sfera session the same way mutations are — Sfera's
/// <c>Uchwyt</c> is not thread-safe (the adapter routes through the single-writer
/// write queue).
/// <para>
/// Error contract: a missing document id surfaces as a failure whose
/// <see cref="Error.Code"/> is <c>not_found</c> (maps to HTTP 404). Transient infra
/// failures use <c>unreachable</c> (503); anything else is <c>rejected</c> (422).
/// </para>
/// </summary>
public interface IInvoicePdfRenderer
{
    Task<Result<byte[]>> RenderAsync(int documentId, CancellationToken cancellationToken = default);
}
