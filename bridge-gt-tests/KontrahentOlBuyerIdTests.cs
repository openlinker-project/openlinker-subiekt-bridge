using Xunit;

/// <summary>
/// The two PURE pieces of the OpenLinker-customer-id rung: what counts as such
/// an id, and which column may hold one.
///
/// The rung exists because the symbol is derived from the buyer's NAME, so it
/// is not an identity at all - `KontrahentSymbolTests` pins that two different
/// people called Jan Kowalski derive the same one. The customer id is the
/// identity the symbol never was.
///
/// Both live in `BridgeKeys` with every other pure decision this bridge makes,
/// so these test the REAL functions rather than a copy of them - the matching
/// and stamping that use them need a live Subiekt database and are exercised by
/// the e2e suite instead.
/// </summary>
public class KontrahentOlBuyerIdTests
{
    [Fact]
    public void An_OpenLinker_customer_id_is_recognised()
    {
        Assert.True(BridgeKeys.LooksLikeOlBuyerId("ol_customer_fce2df4d853f4499b955a6bb1a212bd1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DW 1732Y")]           // a real operator code, observed live on kh_Pole1
    [InlineData("ol_product_abc123")]  // another OpenLinker id, a different entity
    [InlineData("OL_CUSTOMER_ABC")]    // prefix is case-SENSITIVE; ids are minted lowercase
    [InlineData("ol_customer_")]       // the prefix alone identifies nobody
    public void Everything_else_reads_as_UNCLAIMED_never_as_a_claim(string? stored)
    {
        // The field is shared with the operator, so the bridge has to be able to
        // be WRONG about its contents safely. A value it cannot interpret is
        // never a match, and - the half that actually matters - never a RIVAL
        // claim either: reading somebody's warehouse note as another customer's
        // id would refuse a card the buyer legitimately owns and mint a
        // duplicate on every order.
        Assert.False(BridgeKeys.LooksLikeOlBuyerId(stored));
    }

    [Theory]
    [InlineData("Pole1", "kh_Pole1")]
    [InlineData("Pole2", "kh_Pole2")]
    [InlineData("Pole8", "kh_Pole8")]
    [InlineData("pole2", "kh_Pole2")]   // operator-typed config, not an identifier
    [InlineData(" Pole2 ", "kh_Pole2")]
    public void A_configured_field_resolves_to_its_column(string configured, string expected)
    {
        Assert.Equal(expected, ResolveColumn(configured));
    }

    [Theory]
    [InlineData("Pole0")]
    [InlineData("Pole9")]
    [InlineData("kh_Pole2")]            // the COLUMN, not the field - not accepted
    [InlineData("")]
    [InlineData("kh_EMail")]
    [InlineData("Pole2; DROP TABLE kh__Kontrahent--")]
    [InlineData("Pole2' OR '1'='1")]
    public void Anything_else_resolves_to_NOTHING_which_is_also_the_injection_defence(string configured)
    {
        // A column name cannot be a SQL parameter, so this value is
        // interpolated into the statement text. The allowlist is what stands in
        // for a parameter: null here means no statement is built at all, and
        // the feature is simply off.
        //
        // It deliberately does not fall back to `Pole2`. An operator naming
        // another column is most likely doing it BECAUSE Pole2 is taken, so a
        // fallback would write into exactly the data they steered away from.
        Assert.Null(ResolveColumn(configured));
    }

    private static string? ResolveColumn(string configured)
        => BridgeKeys.ResolveKontrahentOlIdColumn(configured);
}

/// <summary>
/// The address-less carve-out, and the one caller that refuses it.
///
/// `MatchesAddress` itself needs a Subiekt database, so what is testable here
/// is the DECISION it delegates - which is the whole of the third review's
/// finding 6: the order path accepts a symbol-only match and the invoice path
/// must not, because only one of them carries an identifier to fall back on.
/// </summary>
public class SymbolOnlyMatchTests
{
    [Fact]
    public void The_ORDER_path_accepts_a_symbol_only_match()
    {
        // Refusing here mints a kontrahent on every marketplace order carrying
        // no invoice address - the common case. Measured live as 102 then 103
        // for one buyer upserted twice.
        Assert.True(BridgeKeys.AcceptsSymbolOnlyMatch(phoneComparable: false, refuseSymbolOnly: false));
    }

    [Fact]
    public void The_INVOICE_path_REFUSES_it_and_takes_the_duplicate_instead()
    {
        // `IssueInvoiceCommand` carries no customer id, so on an invoice with no
        // `zkId` a symbol match is all there is - and two Jan Kowalskis would
        // share one card on a fiscal document.
        Assert.False(BridgeKeys.AcceptsSymbolOnlyMatch(phoneComparable: false, refuseSymbolOnly: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_comparable_phone_settles_it_for_BOTH_paths(bool refuseSymbolOnly)
    {
        // The refusal is a fallback for having nothing to compare, never an
        // override of a real comparison - so a phone present on both sides
        // answers the same way whoever is asking.
        Assert.True(BridgeKeys.AcceptsSymbolOnlyMatch(phoneComparable: true, refuseSymbolOnly));
    }
}
