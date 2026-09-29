using Xunit;

/// <summary>
/// `MakeSymbol` and `DigitsOnly`, the two string reductions kontrahent matching
/// rests on.
///
/// The symbol is NOT an identity, and the tests below say so out loud, because
/// a reader could easily take a 16-character uppercase string for a hash. It is
/// the buyer's name with the punctuation dropped: every "Jan Kowalski" in
/// Poland derives the same one. That is why a symbol match is verified against
/// the address rather than trusted, and it is the measure of what the
/// address-less carve-out actually risks.
/// </summary>
public class KontrahentSymbolTests
{
    [Fact]
    public void Two_different_people_with_the_SAME_NAME_derive_the_same_symbol()
    {
        // The carve-out's real risk, pinned rather than described. Not a hash
        // collision - the common case.
        Assert.Equal(BridgeKeys.MakeSymbol("Jan Kowalski"), BridgeKeys.MakeSymbol("Jan Kowalski"));
    }

    [Fact]
    public void Two_surnames_sharing_a_sixteen_character_prefix_also_collide()
    {
        var a = BridgeKeys.MakeSymbol("Aleksandrowiczowa Anna");
        var b = BridgeKeys.MakeSymbol("Aleksandrowiczowa Barbara");
        Assert.Equal(a, b);
        Assert.Equal(16, a.Length);
    }

    [Fact]
    public void Punctuation_and_spacing_are_dropped_not_preserved()
    {
        Assert.Equal(BridgeKeys.MakeSymbol("Jan Kowalski"), BridgeKeys.MakeSymbol("Jan. Kowalski!"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void A_name_with_nothing_usable_falls_back_rather_than_producing_an_empty_symbol(string name)
    {
        // An empty symbol would match every record with an empty symbol, which
        // is the one thing worse than a duplicate.
        Assert.Equal("ZAM-ANON", BridgeKeys.MakeSymbol(name));
    }

    [Fact]
    public void The_fallback_prefix_is_per_call_so_two_paths_do_not_share_one_anon_record()
    {
        Assert.NotEqual(BridgeKeys.MakeSymbol("", "ZAM"), BridgeKeys.MakeSymbol("", "INV"));
    }

    [Theory]
    [InlineData("123-456-78-90", "1234567890")]
    [InlineData(" 1234567890 ", "1234567890")]
    [InlineData("PL1234567890", "1234567890")]
    [InlineData(null, "")]
    public void A_NIP_reduces_to_its_digits_so_two_spellings_are_one_buyer(string? raw, string expected)
    {
        // Matching on the raw string made `123-456-78-90` and `1234567890` two
        // buyers, each minting its own kontrahent. Both sides are reduced,
        // because the stored one is whatever an operator typed years ago.
        Assert.Equal(expected, BridgeKeys.DigitsOnly(raw));
    }
}
