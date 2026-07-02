using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Infrastructure.Sfera.Adapters;

/// <summary>
/// <see cref="IDefaultBankAccountWriter"/> implemented over
/// <see cref="SferaRachunkiBankoweService"/>, routed through
/// <see cref="SferaWriteQueue"/> (single serialized write worker — the flip is a
/// Podmiot business-object save).
/// </summary>
public sealed class SferaDefaultBankAccountWriter : IDefaultBankAccountWriter
{
    private readonly SferaWriteQueue _queue;
    private readonly SferaRachunkiBankoweService _rachunki;

    public SferaDefaultBankAccountWriter(SferaWriteQueue queue, SferaRachunkiBankoweService rachunki)
    {
        _queue = queue;
        _rachunki = rachunki;
    }

    public async Task<Result> SetDefaultAsync(int bankAccountId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _queue.EnqueueAsync(session => _rachunki.UstawRachunekPodstawowy(session, bankAccountId), cancellationToken)
                .ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex)
        {
            var bex = BridgeException.Classify(ex);
            return Result.Failure(new Error(bex.CodeString, bex.Reason));
        }
    }
}
