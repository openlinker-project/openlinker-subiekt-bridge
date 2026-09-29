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
