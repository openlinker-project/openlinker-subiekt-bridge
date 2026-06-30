using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace SferaApi;

/// <summary>
/// Stable per-document HMAC-SHA256 token for the PDF download URL (issue #3's
/// recommended default). The token is <c>base64url(HMAC-SHA256(secret, documentId))</c>:
/// no expiry, scoped to one document id, signed with the dedicated
/// <see cref="PdfOptions.UrlSigningSecret"/> (NOT the global ApiKey). The PDF route
/// is served WITHOUT the static Bearer (the browser opens it as a plain anchor with
/// no Authorization header); this token is the sole auth for that route.
/// <para>
/// Validation is constant-time (<see cref="CryptographicOperations.FixedTimeEquals"/>)
/// and fails closed when no secret is configured. The token is never logged.
/// </para>
/// </summary>
public sealed class PdfUrlSigner
{
    // The PDF download route shape, shared so the signed-URL builder and the auth
    // middleware's Bearer-exemption matcher (Program.cs) stay in lockstep with the
    // actual MapGet registration. A rename only needs to change these two consts.
    public const string RoutePrefix = "/api/invoices/";
    public const string RouteSuffix = "/pdf";

    private readonly byte[] _secret;
    private readonly string _publicBaseUrl;

    public PdfUrlSigner(IOptions<PdfOptions> options)
    {
        var secret = options.Value.UrlSigningSecret;
        _secret = string.IsNullOrWhiteSpace(secret) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(secret);
        _publicBaseUrl = options.Value.PublicBaseUrl ?? "";
    }

    /// <summary>True when a signing secret is configured (otherwise the route fails closed).</summary>
    public bool IsConfigured => _secret.Length > 0;

    /// <summary>Compute the base64url token for a document id. Throws if no secret is configured.</summary>
    public string Sign(int documentId)
    {
        if (_secret.Length == 0)
            throw new InvalidOperationException("Pdf:UrlSigningSecret is not configured — cannot sign PDF URLs.");
        return Base64Url(Hmac(documentId));
    }

    /// <summary>
    /// Constant-time validation of a presented token against the expected signature
    /// for <paramref name="documentId"/>. Accepts base64url (the form we emit) and,
    /// defensively, lowercase hex. Returns false for a missing token or unconfigured
    /// secret (fail-closed) without leaking timing about the secret content.
    /// </summary>
    public bool Validate(int documentId, string? presentedToken)
    {
        if (_secret.Length == 0 || string.IsNullOrEmpty(presentedToken)) return false;

        var expected = Hmac(documentId);

        // Try BOTH accepted encodings independently (not first-decode-wins): a 64-char
        // hex string also parses as base64 but to the wrong bytes, so we must still
        // check the hex interpretation. Each comparison is constant-time.
        var b64 = TryDecodeBase64Url(presentedToken);
        if (b64 != null && CryptographicOperations.FixedTimeEquals(b64, expected)) return true;

        var hex = TryDecodeHex(presentedToken);
        if (hex != null && CryptographicOperations.FixedTimeEquals(hex, expected)) return true;

        return false;
    }

    private byte[] Hmac(int documentId)
    {
        using var h = new HMACSHA256(_secret);
        return h.ComputeHash(Encoding.UTF8.GetBytes(documentId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[]? TryDecodeBase64Url(string token)
    {
        try
        {
            var s = token.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; case 1: return null; }
            return Convert.FromBase64String(s);
        }
        catch { return null; }
    }

    private static byte[]? TryDecodeHex(string token)
    {
        // Convert.FromHexString is case-insensitive and throws on odd length / non-hex.
        try { return Convert.FromHexString(token); }
        catch (FormatException) { return null; }
    }

    /// <summary>
    /// Build the absolute signed pdfUrl for a document. Prefers the configured
    /// <see cref="PdfOptions.PublicBaseUrl"/>; otherwise uses the supplied request base
    /// (scheme://host). Returns null when no secret is configured (so callers emit null
    /// rather than an unusable URL).
    /// </summary>
    public string? BuildUrl(int documentId, string requestBase)
    {
        if (!IsConfigured) return null;
        var baseUrl = string.IsNullOrWhiteSpace(_publicBaseUrl) ? requestBase : _publicBaseUrl;
        baseUrl = baseUrl.TrimEnd('/');
        return $"{baseUrl}{RoutePrefix}{documentId}{RouteSuffix}?t={Sign(documentId)}";
    }

    /// <summary>
    /// Compose the request's own scheme+host base, e.g. "https://host:5005". Returns
    /// an empty string when the Host header is absent (HTTP/1.0 / malformed) so callers
    /// fall back rather than emit a malformed "https:///..." URL with an empty authority.
    /// </summary>
    public static string RequestBase(Microsoft.AspNetCore.Http.HttpRequest req) =>
        req.Host.HasValue ? $"{req.Scheme}://{req.Host.Value}" : "";
}
