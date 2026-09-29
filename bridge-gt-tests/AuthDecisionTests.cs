using Xunit;
using static BridgeKeys;

/// <summary>
/// The Basic-auth decision, and specifically its ORDER.
///
/// "Unconfigured means CLOSED" is only true if unconfigured is decided BEFORE
/// anything is compared. With blank credentials an equality check matches a
/// request that also sent blanks, so an unconfigured bridge would be an OPEN
/// one - the direction a missing credential must never fail in, on routes that
/// issue documents and move stock.
///
/// `Program.cs` is a file of top-level statements with COM behind it and cannot
/// be hosted by `WebApplicationFactory`, so the decision was extracted rather
/// than the pipeline asserted. What is pinned here is the property that
/// matters; the middleware calls exactly this.
/// </summary>
public class AuthDecisionTests
{
    /// <summary>Stand-in for the constant-time comparison; ordinary equality is
    /// enough to exercise the DECISION, which is what these tests are about.</summary>
    private static bool Eq(string a, string b) => a == b;

    private static string Basic(string user, string pass) =>
        "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(user + ":" + pass));

    [Fact]
    public void Unconfigured_is_decided_BEFORE_the_comparison()
    {
        // THE ORDERING. Blank on both sides, and a request that sends blanks:
        // an equality-first implementation answers Allowed here.
        var outcome = DecideBasicAuth(false, Basic("", ""), "", "", Eq);
        Assert.Equal(BasicAuthOutcome.NotConfigured, outcome);
    }

    [Fact]
    public void Unconfigured_refuses_even_correct_looking_credentials()
    {
        Assert.Equal(BasicAuthOutcome.NotConfigured,
            DecideBasicAuth(false, Basic("admin", "s3cret"), "admin", "s3cret", Eq));
    }

    [Fact]
    public void Configured_and_correct_is_allowed()
    {
        Assert.Equal(BasicAuthOutcome.Allowed,
            DecideBasicAuth(true, Basic("admin", "s3cret"), "admin", "s3cret", Eq));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer something")]
    public void A_header_that_is_not_Basic_is_missing_rather_than_malformed(string? header)
    {
        Assert.Equal(BasicAuthOutcome.Missing, DecideBasicAuth(true, header, "u", "p", Eq));
    }

    [Theory]
    [InlineData("Basic !!!not-base64!!!")]
    [InlineData("Basic ")]
    public void Rubbish_after_Basic_is_MALFORMED_not_a_500(string header)
    {
        // `Convert.FromBase64String` throws, which surfaced as a 500 and reads
        // like the bridge is broken rather than like bad credentials.
        Assert.Equal(BasicAuthOutcome.Malformed, DecideBasicAuth(true, header, "u", "p", Eq));
    }

    [Fact]
    public void Credentials_with_no_colon_are_malformed_rather_than_a_username()
    {
        var header = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("nocolon"));
        Assert.Equal(BasicAuthOutcome.Malformed, DecideBasicAuth(true, header, "u", "p", Eq));
    }

    [Theory]
    [InlineData("admin", "wrong")]
    [InlineData("wrong", "s3cret")]
    public void Wrong_credentials_are_refused_and_told_apart_from_malformed_ones(string u, string p)
    {
        Assert.Equal(BasicAuthOutcome.WrongCredentials,
            DecideBasicAuth(true, Basic(u, p), "admin", "s3cret", Eq));
    }

    [Fact]
    public void A_password_containing_a_colon_survives_the_split()
    {
        // Split on the FIRST colon: a password may legitimately contain one,
        // and splitting on the last would refuse a valid credential.
        Assert.Equal(BasicAuthOutcome.Allowed,
            DecideBasicAuth(true, Basic("admin", "a:b:c"), "admin", "a:b:c", Eq));
    }
}
