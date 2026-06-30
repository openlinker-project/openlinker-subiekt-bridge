using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Infrastructure.Sfera.Adapters;

/// <summary>
/// <see cref="IWarehouseReceiver"/> — warehouse goods receipt (przyjęcie magazynowe /
/// przychód wewnętrzny — PW) through the live Sfera API, routed through <see cref="SferaWriteQueue"/>.
/// <para>
/// Creates a real PW document via <see cref="SferaPrzyjeciaService"/> so stock moves through
/// Subiekt's own accounting (document + valuation + batches). This replaces the legacy raw
/// SQL MERGE into <c>StanyMagazynowe</c> that bypassed Sfera and corrupted stock accounting
/// (deliberately 501'd in Faza 0). The entry/stock-movement date comes from the domain
/// <see cref="IClock"/>; mutations serialize on the single write worker.
/// </para>
/// </summary>
public sealed class SferaWarehouseReceiver : IWarehouseReceiver
{
    private readonly SferaWriteQueue _queue;
    private readonly SferaPrzyjeciaService _przyjecia;
    private readonly IClock _clock;

    public SferaWarehouseReceiver(SferaWriteQueue queue, SferaPrzyjeciaService przyjecia, IClock clock)
    {
        _queue = queue;
        _przyjecia = przyjecia;
        _clock = clock;
    }

    public async Task<Result<WarehouseReceiptRef>> ReceiveAsync(
        string symbol,
        decimal quantity,
        string magazyn,
        string? batchNumber = null,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        var input = new SferaReceiptInput(
            Symbol: symbol,
            Ilosc: quantity,
            Magazyn: string.IsNullOrWhiteSpace(magazyn) ? "MAG" : magazyn,
            Opis: note,
            NumerPartii: batchNumber,
            DataPrzyjecia: _clock.Now.LocalDateTime);

        try
        {
            var (id, numer) = await _queue.EnqueueAsync(
                session => _przyjecia.Utworz(session, input),
                cancellationToken).ConfigureAwait(false);

            return Result.Success(new WarehouseReceiptRef(id, numer));
        }
        catch (Exception ex)
        {
            var bex = BridgeException.Classify(ex);
            return Result.Failure<WarehouseReceiptRef>(new Error(bex.CodeString, bex.Reason));
        }
    }
}
