namespace SferaApi;

// Bound from the top-level "Pdf" section. Configures the signed-URL scheme for the
// invoice PDF download endpoint (GET /api/invoices/{id}/pdf).
public class PdfOptions
{
    // HMAC-SHA256 signing secret for the per-document download token. This is a
    // DEDICATED secret, NOT the global Auth:ApiKey — leaking a doc URL must not
    // grant API access. SECRET: supply via env (Pdf__UrlSigningSecret) or
    // user-secrets, NEVER in a committed file. When empty, the endpoint cannot
    // validate tokens and rejects every request (fail-closed).
    public string UrlSigningSecret { get; set; } = "";

    // Browser-facing absolute base URL the operator's browser uses to reach the
    // bridge (scheme + host[:port], no trailing slash), e.g. https://bridge.example:5005.
    // Used to build the absolute pdfUrl embedded in issue/status responses. When empty,
    // the bridge falls back to the request's own scheme+host.
    public string PublicBaseUrl { get; set; } = "";
}
