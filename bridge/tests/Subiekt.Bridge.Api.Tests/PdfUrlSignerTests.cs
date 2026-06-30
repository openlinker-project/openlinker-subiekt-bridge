using Microsoft.Extensions.Options;
using SferaApi;
using Xunit;

namespace Subiekt.Bridge.Api.Tests;

// Focused tests for the signed-URL auth of the PDF download endpoint. Covers the
// security-critical behaviours: round-trip validity, per-document scoping, tamper
// rejection, fail-closed when no secret is configured, and cross-format decoding.
public class PdfUrlSignerTests
{
    private static PdfUrlSigner Signer(string secret = "test-pdf-secret-2026", string publicBaseUrl = "")
        => new(Options.Create(new PdfOptions { UrlSigningSecret = secret, PublicBaseUrl = publicBaseUrl }));

    [Fact]
    public void Sign_then_Validate_roundtrips_for_same_doc()
    {
        var s = Signer();
        var token = s.Sign(100360);
        Assert.True(s.Validate(100360, token));
    }

    [Fact]
    public void Token_is_scoped_to_one_document()
    {
        var s = Signer();
        var token = s.Sign(100360);
        Assert.False(s.Validate(100358, token));   // token for another doc must not validate
    }

    [Fact]
    public void Tampered_token_is_rejected()
    {
        var s = Signer();
        var token = s.Sign(100360);
        var tampered = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');
        Assert.False(s.Validate(100360, tampered));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64-or-hex-!!!")]
    public void Missing_or_garbage_token_is_rejected(string? token)
    {
        var s = Signer();
        Assert.False(s.Validate(100360, token));
    }

    [Fact]
    public void Unconfigured_secret_fails_closed()
    {
        var s = Signer(secret: "");
        Assert.False(s.IsConfigured);
        Assert.False(s.Validate(100360, "anything"));
        Assert.Null(s.BuildUrl(100360, "https://host:5005"));   // no usable URL emitted
    }

    [Fact]
    public void Hex_encoding_of_the_signature_also_validates()
    {
        var s = Signer();
        // Recompute the raw HMAC and present it as lowercase hex (defensive accept path).
        using var h = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes("test-pdf-secret-2026"));
        var raw = h.ComputeHash(System.Text.Encoding.UTF8.GetBytes("100360"));
        var hex = Convert.ToHexString(raw).ToLowerInvariant();
        Assert.True(s.Validate(100360, hex));
    }

    [Fact]
    public void BuildUrl_prefers_configured_public_base_and_embeds_token()
    {
        var s = Signer(publicBaseUrl: "https://bridge.example:5005/");
        var url = s.BuildUrl(100360, "https://ignored-request-host");
        Assert.NotNull(url);
        Assert.StartsWith("https://bridge.example:5005/api/invoices/100360/pdf?t=", url);
        // The embedded token validates for this doc.
        var token = url!.Split("?t=")[1];
        Assert.True(s.Validate(100360, token));
    }

    [Fact]
    public void BuildUrl_falls_back_to_request_base_when_no_public_base()
    {
        var s = Signer();
        var url = s.BuildUrl(100360, "https://req-host:5005");
        Assert.StartsWith("https://req-host:5005/api/invoices/100360/pdf?t=", url);
    }
}
