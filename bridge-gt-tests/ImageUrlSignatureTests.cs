using Xunit;

/// <summary>
/// The /gt-image gate.
///
/// That route is excluded from BOTH auth middlewares and takes a sequential
/// integer, so before this anyone who could reach the port could count 1..N
/// and take the seller's whole product-image library. A bearer token cannot
/// fix it: a browser and a marketplace fetch these URLs and neither attaches a
/// header OpenLinker chose.
///
/// These tests run with no InvoiceToken configured, which is itself one of the
/// two behaviours worth pinning - see below.
/// </summary>
public class ImageUrlSignatureTests
{
    /// <summary>
    /// UNCONFIGURED SERVES. A bridge with no token has no secret to sign with,
    /// and refusing there would black out every product image on an install
    /// that was working a moment ago. The token gate already fails such a
    /// bridge closed on /api, which is where the damage would be.
    /// </summary>
    [Fact]
    public void With_no_secret_configured_nothing_is_signed_and_nothing_is_refused()
    {
        Assert.Equal("", ImageUrlSignature.For(4));
        Assert.Equal("", ImageUrlSignature.QuerySuffix(4));
        Assert.True(ImageUrlSignature.IsValid(4, null));
        Assert.True(ImageUrlSignature.IsValid(4, "anything"));
    }

    /// <summary>
    /// The suffix is empty or a complete query parameter - never a bare `?`,
    /// which would turn a working URL into one a marketplace may reject.
    /// </summary>
    [Fact]
    public void Suffix_is_either_empty_or_a_whole_parameter()
    {
        var suffix = ImageUrlSignature.QuerySuffix(4);
        Assert.True(suffix == "" || suffix.StartsWith($"?{ImageUrlSignature.ParameterName}="));
    }
}
