using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Application.Ports;

/// <summary>
/// The KSeF (Krajowy System e-Faktur) status of a document, mapped onto the OL
/// contract's five values: <c>none</c>, <c>pending</c>, <c>sent</c>,
/// <c>accepted</c>, <c>rejected</c>.
/// </summary>
public sealed record KsefStatus(string Status, bool Submitted, string? Reference);

/// <summary>
/// Status of a previously issued document. A document carrying a full number
/// (sygnatura) is <c>zatwierdzony</c> (issued); without one it is <c>bufor</c>
/// (draft). When the id is unknown <see cref="Status"/> is <c>not_found</c>.
/// </summary>
public sealed record DocumentStatus(
    int Id,
    string Status,
    string? Numer,
    decimal? Netto,
    decimal? Vat,
    decimal? Brutto,
    DateTime? CreatedAt,
    KsefStatus? Ksef);

/// <summary>Read-only port for the status of issued documents.</summary>
public interface IDocumentStatusReader
{
    /// <summary>
    /// Read the status of document <paramref name="documentId"/>. Returns a
    /// success result whose value's <see cref="DocumentStatus.Status"/> is
    /// <c>not_found</c> when no such document exists.
    /// </summary>
    Task<Result<DocumentStatus>> GetStatusAsync(int documentId, CancellationToken cancellationToken = default);
}
