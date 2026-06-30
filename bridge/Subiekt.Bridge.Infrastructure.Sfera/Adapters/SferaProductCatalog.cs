using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Infrastructure.Sfera.Adapters;

/// <summary>
/// <see cref="IProductCatalog"/> (write/upsert side) implemented over the moved
/// <see cref="SferaAsortymentyService"/>, routed through <see cref="SferaWriteQueue"/>.
/// <para>
/// READ-side product queries belong to the 3A <see cref="IProductCatalogReader"/>
/// (SQL); this port is only the write path (Exists check + upsert) and goes through
/// the serialized Sfera session like every other mutation. (ExistsAsync runs on the
/// write worker too: it reads through the live Sfera SQL connection, and keeping it
/// on the queue avoids touching the session from a second thread.)
/// </para>
/// </summary>
public sealed class SferaProductCatalog : IProductCatalog
{
    private readonly SferaWriteQueue _queue;
    private readonly SferaAsortymentyService _asortymenty;

    public SferaProductCatalog(SferaWriteQueue queue, SferaAsortymentyService asortymenty)
    {
        _queue = queue;
        _asortymenty = asortymenty;
    }

    public async Task<Result<bool>> ExistsAsync(string symbol, CancellationToken cancellationToken = default)
    {
        try
        {
            var exists = await _queue.EnqueueAsync(session => _asortymenty.Exists(session, symbol), cancellationToken)
                .ConfigureAwait(false);
            return Result.Success(exists);
        }
        catch (Exception ex)
        {
            var bex = BridgeException.Classify(ex);
            return Result.Failure<bool>(new Error(bex.CodeString, bex.Reason));
        }
    }

    public async Task<Result<ProductRef>> UpsertAsync(
        string symbol,
        string nazwa,
        decimal cenaEwidencyjna,
        string? wzorzecSymbol = null,
        CancellationToken cancellationToken = default)
    {
        var input = new SferaProductInput(
            Symbol: symbol,
            Nazwa: nazwa,
            Opis: null,
            CenaEwidencyjna: cenaEwidencyjna,
            WzorzecSymbol: wzorzecSymbol);

        try
        {
            var (id, sym, created) = await _queue.EnqueueAsync(
                session => _asortymenty.UpsertTowar(session, input),
                cancellationToken).ConfigureAwait(false);

            return Result.Success(new ProductRef(id, sym, created));
        }
        catch (Exception ex)
        {
            var bex = BridgeException.Classify(ex);
            return Result.Failure<ProductRef>(new Error(bex.CodeString, bex.Reason));
        }
    }
}
