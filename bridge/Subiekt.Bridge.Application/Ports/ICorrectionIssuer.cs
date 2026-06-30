using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Application.Ports;

/// <summary>
/// A requested correction to one original document line. At least one of
/// <paramref name="NewQuantity"/> / <paramref name="NewUnitPriceGross"/> is set
/// (the API validator enforces this). <paramref name="NewUnitPriceGross"/> is a
/// GROSS unit price.
/// </summary>
public readonly record struct CorrectionLine(int Lp, decimal? NewQuantity, decimal? NewUnitPriceGross);

/// <summary>
/// Port for issuing corrections (faktura korygująca) against an existing document.
/// Minimal interface stub; fleshed out in a later phase.
/// </summary>
public interface ICorrectionIssuer
{
    Task<Result<DocumentRef>> IssueCorrectionAsync(
        int originalDocumentId,
        string? reason,
        IReadOnlyList<CorrectionLine> lines,
        CancellationToken cancellationToken = default);
}
