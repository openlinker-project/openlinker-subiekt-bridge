using Xunit;

/// <summary>
/// `BridgeKeys.ReduceIdempotencyKey`, pinned.
///
/// This is the function that decides whether a retried operation is recognised
/// as the same operation. It exists because `Trim30` is not a size fix but a
/// silent collision: every OpenLinker key of this shape leads with a fixed
/// prefix and ends with the part that tells two operations apart, so cutting
/// at 30 keeps the sameness and discards the difference. The measured
/// consequences were a restock reported "deduplicated" while no stock moved,
/// and every document for one connection collapsing onto the first ever issued.
///
/// So these are not style assertions. A change that breaks one of them changes
/// whether a customer is billed twice.
/// </summary>
public class IdempotencyKeyReductionTests
{
    [Fact]
    public void Short_keys_pass_through_untouched()
    {
        // The column holds 30. Anything that already fits must not be rewritten,
        // or every key written before this function existed stops being found.
        const string key = "invoice:abc:123";
        Assert.Equal(key, BridgeKeys.ReduceIdempotencyKey(key));
    }

    [Fact]
    public void A_key_of_exactly_thirty_is_still_passed_through()
    {
        var key = new string('a', 30);
        Assert.Equal(key, BridgeKeys.ReduceIdempotencyKey(key));
    }

    [Fact]
    public void A_reduced_key_fits_the_column()
    {
        var key = "invoice:" + new string('b', 200);
        Assert.True(BridgeKeys.ReduceIdempotencyKey(key).Length <= 30);
    }

    [Fact]
    public void The_same_key_always_reduces_to_the_same_value()
    {
        // Stability across CALLS is what makes store-then-look-up work at all.
        // It must also hold across PROCESSES, which is why the reduction is a
        // hash of the string and never anything seeded per run.
        var key = "return:" + new string('c', 120) + ":7";
        Assert.Equal(BridgeKeys.ReduceIdempotencyKey(key), BridgeKeys.ReduceIdempotencyKey(key));
    }

    [Fact]
    public void Keys_differing_only_AFTER_the_thirtieth_character_reduce_differently()
    {
        // THE WHOLE POINT. `Trim30` answers the same string for both of these,
        // which is how one return's second line was answered with the first
        // line's result and no stock moved for it.
        var shared = "return:" + new string('d', 60) + ":";
        var first = BridgeKeys.ReduceIdempotencyKey(shared + "1");
        var second = BridgeKeys.ReduceIdempotencyKey(shared + "2");
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Trim30_collides_on_exactly_that_pair_which_is_why_this_function_exists()
    {
        // Asserted rather than described, so the contrast cannot rot: if a
        // future edit made Trim30 safe for these, this test says so out loud.
        var shared = "return:" + new string('d', 60) + ":";
        Assert.Equal(BridgeKeys.Trim30(shared + "1"), BridgeKeys.Trim30(shared + "2"));
    }
}
