using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Subiekt.Bridge.Infrastructure.Sfera;

/// <summary>
/// The single background worker that drains <see cref="SferaWriteQueue"/>. Runs one
/// long-lived loop: dequeue a work item, ensure the session is connected, run the
/// delegate under the session lock, and publish the outcome on the item's
/// <see cref="TaskCompletionSource{TResult}"/>. Being the ONLY consumer is what
/// serializes Sfera mutations (Sfera is not thread-safe).
/// </summary>
public sealed class SferaWriteQueueConsumer : BackgroundService
{
    private readonly SferaWriteQueue _queue;
    private readonly SferaSession _session;
    private readonly ILogger<SferaWriteQueueConsumer> _log;

    public SferaWriteQueueConsumer(SferaWriteQueue queue, SferaSession session, ILogger<SferaWriteQueueConsumer> log)
    {
        _queue = queue;
        _session = session;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Sfera write worker started (single-consumer serialized mutations)");

        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                // If the caller cancelled before we picked the item up, don't run it.
                if (item.CancellationToken.IsCancellationRequested)
                {
                    item.Completion.TrySetCanceled(item.CancellationToken);
                    continue;
                }

                try
                {
                    // Ensure a live session (connect / reconnect on a stale one) and
                    // run the mutation under the session lock. EnsureConnected is
                    // reentrant; the lock guards against a concurrent /health probe
                    // and any residual lock(SyncRoot) callers.
                    _session.EnsureConnected();

                    object? result;
                    lock (_session.SyncRoot)
                    {
                        result = item.Work(_session);
                    }

                    item.Completion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    // Surface the original exception to the awaiting caller; the
                    // adapter classifies it (unreachable vs rejected). The worker
                    // itself never dies on a single failed work item.
                    _log.LogWarning(ex, "Sfera write item failed: {msg}", ex.Message);
                    item.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _log.LogInformation("Sfera write worker stopping (host shutdown)");
        }
        finally
        {
            // No more work will be enqueued; close the channel so any racing writer
            // gets the shutdown rejection instead of a silently-dropped item.
            _queue.Complete();

            // Drain whatever was already BUFFERED in the channel and fault each item,
            // so their awaiting callers fail gracefully (as the queue's XML doc promises)
            // instead of hanging forever once the worker has stopped. ExecuteAsync's
            // finally runs exactly once, so this drain runs exactly once on shutdown.
            // TrySetException is first-wins and never throws on an already-completed item.
            while (_queue.Reader.TryRead(out var leftover))
            {
                leftover.Completion.TrySetException(new BridgeException(
                    BridgeErrorCode.Unreachable,
                    "Bridge is shutting down; Sfera operation not executed."));
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Complete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
