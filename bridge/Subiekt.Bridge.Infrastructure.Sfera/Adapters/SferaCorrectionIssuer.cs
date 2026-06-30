using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Infrastructure.Sfera.Adapters;

/// <summary>
/// <see cref="ICorrectionIssuer"/> implemented over the moved
/// <see cref="SferaKorektyService"/>, routed through <see cref="SferaWriteQueue"/>.
/// Maps the port's <see cref="CorrectionLine"/> list onto the Infrastructure-local
/// <see cref="SferaCorrectionLineInput"/> and returns the issued correction's id/number.
/// </summary>
public sealed class SferaCorrectionIssuer : ICorrectionIssuer
{
    private readonly SferaWriteQueue _queue;
    private readonly SferaKorektyService _korekty;

    public SferaCorrectionIssuer(SferaWriteQueue queue, SferaKorektyService korekty)
    {
        _queue = queue;
        _korekty = korekty;
    }

    public async Task<Result<DocumentRef>> IssueCorrectionAsync(
        int originalDocumentId,
        string? reason,
        IReadOnlyList<CorrectionLine> lines,
        CancellationToken cancellationToken = default)
    {
        var input = new SferaCorrectionInput(
            Przyczyna: reason,
            Lines: (lines ?? Array.Empty<CorrectionLine>())
                .Select(l => new SferaCorrectionLineInput(l.Lp, l.NewQuantity, l.NewUnitPriceGross))
                .ToList());

        try
        {
            var (id, numer) = await _queue.EnqueueAsync(
                session => _korekty.UtworzKorekte(session, originalDocumentId, input),
                cancellationToken).ConfigureAwait(false);

            return Result.Success(new DocumentRef(id, numer));
        }
        catch (Exception ex)
        {
            var bex = BridgeException.Classify(ex);
            return Result.Failure<DocumentRef>(new Error(bex.CodeString, bex.Reason));
        }
    }
}
