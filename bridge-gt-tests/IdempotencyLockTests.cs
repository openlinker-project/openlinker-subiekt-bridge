using Xunit;

/// <summary>
/// `IdempotencyLock`, pinned.
///
/// It is what turns the check-then-create sequence in `CreateOrder` from a race
/// into a decision. Without it two overlapping calls under one order ref both
/// observe "no existing ZK" and both create one - a second real sales order for
/// one sale, which is not something an operator can undo by clicking.
///
/// It is an IN-PROCESS mutex and nothing more. It does not survive a restart
/// and it does not coordinate two bridge processes; the durable guarantee is
/// the `dok_NrPelnyOryg` lookup it serialises, not the lock itself.
/// </summary>
public class IdempotencyLockTests
{
    [Fact]
    public async Task Two_bodies_under_the_SAME_key_never_overlap()
    {
        // Overlap is the defect. Counting concurrency rather than asserting an
        // order, because the lock promises exclusion and says nothing about
        // which caller wins.
        var concurrent = 0;
        var maxSeen = 0;
        var gate = new object();

        async Task<int> Body()
        {
            lock (gate) { concurrent++; if (concurrent > maxSeen) maxSeen = concurrent; }
            await Task.Delay(30);
            lock (gate) { concurrent--; }
            return 0;
        }

        await Task.WhenAll(
            IdempotencyLock.RunExclusive("order-1", Body),
            IdempotencyLock.RunExclusive("order-1", Body),
            IdempotencyLock.RunExclusive("order-1", Body));

        Assert.Equal(1, maxSeen);
    }

    [Fact]
    public async Task Different_keys_are_NOT_serialised_against_each_other()
    {
        // The other half, and the one a too-broad lock would break: two
        // different orders must not queue behind each other. A bridge that
        // serialised every create would turn a slow COM call into a queue for
        // the whole shop.
        var started = 0;
        var bothStarted = new TaskCompletionSource();

        async Task<int> Body()
        {
            if (Interlocked.Increment(ref started) == 2) bothStarted.SetResult();
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return 0;
        }

        // Completes only if the two run at once; otherwise the second never
        // starts and the first waits out its timeout.
        await Task.WhenAll(
            IdempotencyLock.RunExclusive("order-a", Body),
            IdempotencyLock.RunExclusive("order-b", Body));

        Assert.Equal(2, started);
    }

    [Fact]
    public async Task An_EMPTY_key_runs_unlocked_rather_than_serialising_everything()
    {
        // Deliberate: an order with no ref has no natural key to serialise on,
        // and folding them all onto one lock would serialise unrelated work.
        var ran = false;
        await IdempotencyLock.RunExclusive("", async () => { ran = true; await Task.Yield(); return 0; });
        Assert.True(ran);
    }

    [Fact]
    public async Task A_body_that_throws_RELEASES_the_gate()
    {
        // The failure that turns one bad call into a wedged key: if the gate
        // were not released on the throw path, every later call for that order
        // would hang instead of retrying.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IdempotencyLock.RunExclusive<int>("order-boom", () => throw new InvalidOperationException("boom")));

        var recovered = await IdempotencyLock.RunExclusive("order-boom", () => Task.FromResult(7));
        Assert.Equal(7, recovered);
    }
}
