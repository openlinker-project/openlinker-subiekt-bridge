using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Subiekt.Bridge.Infrastructure.Sfera;

/// <summary>
/// Serializes ALL Sfera mutations onto a single dedicated worker, replacing the
/// legacy <c>lock(SyncRoot) + Task.Run</c> pattern in each service. Sfera's
/// <c>Uchwyt</c> is not thread-safe for parallel calls, so every write goes
/// through one channel consumed by exactly one background worker
/// (<see cref="SferaWriteQueueConsumer"/>).
/// <para>
/// This gives real async at the HTTP boundary (the request thread awaits a
/// <see cref="TaskCompletionSource{TResult}"/> instead of blocking a thread-pool
/// thread under a lock) while keeping mutations strictly serial. READS do NOT go
/// through this queue — the 3A SQL read-models use a separate connection.
/// </para>
/// <para>
/// Exception surfacing: the work item's exception is captured on the worker and
/// re-thrown to the awaiting caller via <see cref="TaskCompletionSource{TResult}.SetException(Exception)"/>,
/// so <c>await EnqueueAsync(...)</c> throws exactly what the work delegate threw
/// (callers/adapters then classify it via <see cref="BridgeException.Classify"/>).
/// </para>
/// </summary>
public sealed class SferaWriteQueue
{
    // One unit of serialized work: the delegate to run against the live session,
    // a completion source to publish the outcome on, and the caller's token.
    internal sealed class WorkItem
    {
        public required Func<SferaSession, object?> Work { get; init; }
        public required TaskCompletionSource<object?> Completion { get; init; }
        public CancellationToken CancellationToken { get; init; }
    }

    // Unbounded: mutations are low-volume (issue invoice / upsert) and we never
    // want to drop or reject one. Single reader (the consumer), many writers.
    private readonly Channel<WorkItem> _channel =
        Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly ILogger<SferaWriteQueue> _log;

    public SferaWriteQueue(ILogger<SferaWriteQueue> log) => _log = log;

    internal ChannelReader<WorkItem> Reader => _channel.Reader;

    /// <summary>
    /// Enqueue a unit of mutating work and await its result. The delegate runs on
    /// the single write worker, after <see cref="SferaSession.EnsureConnected"/>,
    /// under the session lock. Cancellation before the item starts surfaces as a
    /// cancelled task; the work delegate itself is synchronous and not interrupted
    /// mid-flight (a fiscal mutation must not be torn).
    /// </summary>
    public async Task<T> EnqueueAsync<T>(Func<SferaSession, T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem
        {
            Work = session => work(session),
            Completion = tcs,
            CancellationToken = cancellationToken,
        };

        if (!_channel.Writer.TryWrite(item))
        {
            // An unbounded channel only refuses writes once completed (shutdown).
            tcs.TrySetException(new BridgeException(
                BridgeErrorCode.Unreachable, "Sfera write queue is shutting down — operation not accepted."));
        }

        // Observe the caller's token while the item sits QUEUED: a cancelled caller
        // completes the TCS eagerly instead of hanging until the single worker reaches
        // this item (which may be stuck behind a blocked op like a reconnect).
        // TrySetCanceled races first-wins against the worker's TrySetResult/TrySetException,
        // so a mutation that has already STARTED is never torn — the worker still re-checks
        // IsCancellationRequested before running a queued item.
        using var reg = cancellationToken.Register(static state =>
        {
            var (source, token) = ((TaskCompletionSource<object?>, CancellationToken))state!;
            source.TrySetCanceled(token);
        }, (tcs, cancellationToken));

        // Awaiting the TCS rethrows the worker's captured exception (via SetException)
        // exactly as the work delegate threw it. Unbox object? back to T.
        var result = await tcs.Task.ConfigureAwait(false);
        return (T)result!;
    }

    /// <summary>Non-generic overload for work that returns nothing.</summary>
    public Task EnqueueAsync(Action<SferaSession> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return EnqueueAsync<object?>(session => { work(session); return null; }, cancellationToken);
    }

    /// <summary>Signal no more work will be enqueued (called on host shutdown).</summary>
    internal void Complete() => _channel.Writer.TryComplete();
}
