using Xunit;

/// <summary>
/// `BridgeKeys.AddressesMatch`, the truth table pinned.
///
/// The asymmetry is the point and it is not obvious from the code: a false
/// NEGATIVE mints a duplicate kontrahent, which is untidy and visible; a false
/// POSITIVE bills one person's fiscal document to another's name and address,
/// which the operator cannot see from Subiekt at all. So the rule is "at least
/// one field confirmed equal on both sides, and no field contradicted", never
/// "nothing disagreed".
///
/// This used to fail open whenever EITHER side lacked a field, so a candidate
/// carrying a blank stored address matched ANY incoming buyer.
/// </summary>
public class KontrahentAddressMatchTests
{
    [Theory]
    // both fields agree
    [InlineData("00-001", "Warszawa", "00-001", "Warszawa", true)]
    // one field agrees, the other is absent on one side - confirmed, not contradicted
    [InlineData("00-001", "", "00-001", "Warszawa", true)]
    [InlineData("", "Warszawa", "00-001", "Warszawa", true)]
    // case and padding are not a disagreement
    [InlineData("00-001", "warszawa", "00-001", "WARSZAWA", true)]
    // a contradiction on either field refuses, even when the other agrees
    [InlineData("00-001", "Warszawa", "00-002", "Warszawa", false)]
    [InlineData("00-001", "Warszawa", "00-001", "Krakow", false)]
    // THE ORIGINAL DEFECT: a stored record with no address must not match a
    // buyer who supplied one. Nothing was confirmed, so nothing is claimed.
    [InlineData("00-001", "Warszawa", "", "", false)]
    // and the mirror: a buyer with no address against a stored one confirms
    // nothing either. The carve-out that lets this through lives in the CALLER,
    // deliberately, so it cannot be mistaken for a property of the comparison.
    [InlineData("", "", "00-001", "Warszawa", false)]
    [InlineData("", "", "", "", false)]
    public void The_truth_table(string wantKod, string wantMiasto, string storedKod, string storedMiasto, bool expected)
    {
        Assert.Equal(expected, BridgeKeys.AddressesMatch(wantKod, wantMiasto, storedKod, storedMiasto));
    }
}

/// <summary>
/// The address-less buyer, after PR #7's second review (finding 6).
///
/// Such a buyer used to be accepted on the SYMBOL alone, which merges every
/// "Jan Kowalski" onto one kontrahent and bills a fiscal document to whoever
/// got there first. The phone decides instead - the one discriminating field
/// OpenLinker sends - and with no phone on either side the answer is NO MATCH,
/// so the caller creates its own record. A duplicate is recoverable; a
/// misattributed invoice is not.
/// </summary>
public class AddresslessBuyerTests
{
    [Fact]
    public void A_matching_phone_is_a_match()
    {
        Assert.True(BridgeKeys.AddresslessBuyerMatches("601234567", "601234567"));
    }

    [Theory]
    [InlineData("+48 601 234 567", "601234567")]
    [InlineData("601-234-567", "601234567")]
    [InlineData("0048601234567", "+48601234567")]
    public void Spelling_does_not_make_two_buyers_out_of_one(string want, string stored)
    {
        // Compared on the LAST NINE digits, and this test is why. Comparing all
        // the digits made `+48 601 234 567` a different buyer from `601234567`
        // - the country prefix is exactly what varies between how a marketplace
        // sends a number and how an operator typed it years ago.
        Assert.True(BridgeKeys.AddresslessBuyerMatches(want, stored));
    }

    [Fact]
    public void Two_genuinely_different_numbers_are_not_collapsed_by_that_reduction()
    {
        // The reduction must not be so generous it merges real strangers.
        Assert.False(BridgeKeys.AddresslessBuyerMatches("+48 601 234 567", "+48 602 000 000"));
    }

    [Fact]
    public void A_different_phone_is_not_a_match()
    {
        Assert.False(BridgeKeys.AddresslessBuyerMatches("601234567", "602000000"));
    }

    [Theory]
    [InlineData("", "601234567")]
    [InlineData("601234567", "")]
    [InlineData("", "")]
    public void With_no_phone_to_compare_it_falls_back_to_the_symbol_match(string want, string stored)
    {
        // Measured live, and it overturned the first version of this rule.
        // The reference Subiekt build has NO `Telefon` property - setting it
        // raises "'System.__ComObject' does not contain a definition for
        // 'Telefon'" - so no phone can be stored, so requiring one refuses
        // every address-less buyer and mints a kontrahent per order. That is
        // the unbounded-duplicates defect arriving by way of the fix for a
        // rarer one.
        //
        // The phone therefore TIGHTENS the match where the data exists and
        // never replaces it. Where one side has none, there is nothing to
        // contradict and the symbol match stands.
        Assert.True(BridgeKeys.AddresslessBuyerMatches(want, stored));
    }
}
