using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
// Faza 3B: the Sfera session/boot/services + adapters live in Infrastructure.Sfera.
using Subiekt.Bridge.Infrastructure.Sfera;
using Subiekt.Bridge.Infrastructure.Sfera.Adapters;
using Subiekt.Bridge.Infrastructure.Sql;
using Subiekt.Bridge.Infrastructure.Persistence;
using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Application.UseCases;
using SferaApi;
using SferaApi.Models;
using SferaApi.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// ========================================================================
//  Composition root. Endpoint lambdas live in Endpoints/*Endpoints.cs and
//  are wired at the bottom via app.MapXxxEndpoints(). Faza-4 reorganization:
//  this file is ONLY configuration binding + DI + middleware + endpoint maps.
// ========================================================================

// ---------- Configuration binding ----------
// Bind options first — we need BinariesDir to install the assembly resolver
// before any Sfera type is referenced by the JIT.
builder.Services.Configure<SferaOptions>(builder.Configuration.GetSection("Sfera"));
var sferaOpt = builder.Configuration.GetSection("Sfera").Get<SferaOptions>()
    ?? throw new InvalidOperationException("Missing Sfera config");
SferaBoot.InstallAssemblyResolver(sferaOpt.BinariesDir);

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
var authOpt = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();

// Signed-URL config for the PDF download endpoint. UrlSigningSecret is a SECRET
// (env Pdf__UrlSigningSecret), dedicated to PDF tokens — never the global ApiKey.
builder.Services.Configure<PdfOptions>(builder.Configuration.GetSection("Pdf"));
builder.Services.AddSingleton<PdfUrlSigner>();
var pdfOpt = builder.Configuration.GetSection("Pdf").Get<PdfOptions>() ?? new PdfOptions();

// Diagnostics flag (secure-by-default: off unless configured AND in Development).
builder.Services.Configure<DiagnosticsOptions>(builder.Configuration.GetSection("Diagnostics"));
var diagOpt = builder.Configuration.GetSection("Diagnostics").Get<DiagnosticsOptions>() ?? new DiagnosticsOptions();

// ---------- DI registration ----------

// Infrastructure.Sfera: session + write queue (Faza 3B). One process-wide Sfera
// session; all MUTATIONS go through the single-consumer SferaWriteQueue (serializes
// Sfera, which is not thread-safe). Reads go through the separate SQL connection.
builder.Services.AddSingleton<SferaSession>();
builder.Services.AddSingleton<SferaWriteQueue>();
builder.Services.AddHostedService<SferaWriteQueueConsumer>();   // the single write worker
builder.Services.AddHostedService<SferaConnectHostedService>(); // best-effort auto-connect on startup

// Moved Sfera services (move-and-wrap; reflection logic unchanged).
builder.Services.AddSingleton<SferaPodmiotyService>();
builder.Services.AddSingleton<SferaDokumentySprzedazyService>();
builder.Services.AddSingleton<SferaAsortymentyService>();
builder.Services.AddSingleton<SferaKorektyService>();
builder.Services.AddSingleton<SferaPrzyjeciaService>();
builder.Services.AddSingleton<SferaRachunkiBankoweService>();

// Domain clock (fiscal dates / receipt entry date). One implementation process-wide.
builder.Services.AddSingleton<Subiekt.Bridge.Domain.Common.IClock, SystemClock>();

// Port -> Sfera adapter bindings (Faza 3B).
builder.Services.AddSingleton<ICustomerDirectory, SferaCustomerDirectory>();
builder.Services.AddSingleton<IInvoiceIssuer, SferaInvoiceIssuer>();
builder.Services.AddSingleton<ICorrectionIssuer, SferaCorrectionIssuer>();
builder.Services.AddSingleton<IProductCatalog, SferaProductCatalog>();
builder.Services.AddSingleton<IWarehouseReceiver, SferaWarehouseReceiver>();  // honest 501 stub
builder.Services.AddSingleton<IIssueInvoiceWithBuyer, SferaIssueInvoiceWithBuyer>();
builder.Services.AddSingleton<IDefaultBankAccountWriter, SferaDefaultBankAccountWriter>();
builder.Services.AddSingleton<SferaPdfPrintoutService>();
builder.Services.AddSingleton<IInvoicePdfRenderer, SferaInvoicePdfRenderer>();

// 3A read-models. Read SQL options: prefer an explicit "SqlRead" section; otherwise
// derive a separate read connection from the Sfera Sql* settings so listing never
// contends with the Sfera write session.
builder.Services.AddSingleton(sp =>
{
    var ro = builder.Configuration.GetSection("SqlRead").Get<SqlReadOptions>() ?? new SqlReadOptions();
    if (string.IsNullOrWhiteSpace(ro.ConnectionString)
        && string.IsNullOrWhiteSpace(ro.Server) && string.IsNullOrWhiteSpace(ro.Database))
    {
        // Derive from the Sfera SQL settings (same DB, separate connection).
        ro.Server = sferaOpt.SqlServer;
        ro.Database = sferaOpt.SqlDatabase;
        ro.UseWindowsAuth = sferaOpt.SqlUseWindowsAuth;
        ro.Encrypt = sferaOpt.SqlEncrypt;
        ro.User = sferaOpt.SqlUser;
        ro.Password = sferaOpt.SqlPassword;
    }
    return ro;
});
builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddSingleton<ICustomerQuery, SqlCustomerQuery>();
builder.Services.AddSingleton<IProductCatalogReader, SqlProductCatalogReader>();
builder.Services.AddSingleton<IStockReader, SqlStockReader>();
builder.Services.AddSingleton<IBankAccountsReader, SqlBankAccountsReader>();
builder.Services.AddSingleton<IBranchesReader, SqlBranchesReader>();
builder.Services.AddSingleton<ICashRegistersReader, SqlCashRegistersReader>();
builder.Services.AddSingleton<IDocumentStatusReader, SqlDocumentStatusReader>();

// Idempotency (atomic file store) + audit (SQLite).
builder.Services.AddSingleton(sp =>
    builder.Configuration.GetSection("Idempotency").Get<IdempotencyStoreOptions>()
        ?? new IdempotencyStoreOptions { Directory = builder.Environment.ContentRootPath });
builder.Services.AddSingleton<IIdempotencyStore, AtomicIdempotencyStore>();
builder.Services.AddSingleton(sp =>
    builder.Configuration.GetSection("Audit").Get<SqliteAuditLogOptions>()
        ?? new SqliteAuditLogOptions { DatabasePath = System.IO.Path.Combine(builder.Environment.ContentRootPath, "audit-log.db") });
builder.Services.AddSingleton<IAuditLog, SqliteAuditLog>();

// Application use-cases.
builder.Services.AddSingleton<UpsertCustomerHandler>();

// ---------- TLS / HTTPS server cert ----------
// When the bridge binds an https:// URL (required for any non-loopback exposure,
// see the startup guard below), Kestrel needs a server certificate. We source it
// from a `Tls` config section (CertPath + CertPassword) and load the PFX into
// Kestrel's HTTPS defaults — this applies to EVERY https:// endpoint in app.Urls.
//
//   Tls:CertPath     — path to a .pfx (PKCS#12) file. Relative paths resolve
//                      against the content root.
//   Tls:CertPassword — PFX password. SECRET: supply via env (Tls__CertPassword)
//                      or user-secrets, NEVER in appsettings.
//
// Sourcing:
//   DEV/TEST   — a self-signed PFX, e.g. `dotnet dev-certs https -ep cert.pfx -p <pwd>`.
//                Self-signed: clients must trust it or skip verification (curl -k).
//   PRODUCTION — a real cert from your CA, OR terminate TLS at a reverse proxy and
//                bind the bridge to loopback (no Tls section needed then).
//
// If no Tls:CertPath is set we DON'T touch Kestrel here: loopback-HTTP dev is
// unchanged, and a missing cert on a non-loopback https binding surfaces as a
// clear Kestrel "no certificate" startup error rather than a silent misconfig.
var tlsCertPath = builder.Configuration.GetValue<string?>("Tls:CertPath");
var tlsCertPassword = builder.Configuration.GetValue<string?>("Tls:CertPassword");
if (!string.IsNullOrWhiteSpace(tlsCertPath))
{
    var resolvedCertPath = System.IO.Path.IsPathRooted(tlsCertPath)
        ? tlsCertPath
        : System.IO.Path.Combine(builder.Environment.ContentRootPath, tlsCertPath);

    if (!System.IO.File.Exists(resolvedCertPath))
    {
        throw new InvalidOperationException(
            $"Refusing to start: Tls:CertPath '{resolvedCertPath}' does not exist. " +
            "Provide a valid .pfx (production cert or a dev self-signed cert via " +
            "'dotnet dev-certs https -ep <path> -p <pwd>'), or omit Tls:CertPath and " +
            "bind to loopback behind a TLS-terminating reverse proxy.");
    }

    // Load once at startup; reused for every TLS handshake. Cast to string disambiguates
    // the (string, string?) PFX-password overload from the SecureString one.
    var serverCert = new X509Certificate2(resolvedCertPath, (string?)tlsCertPassword);

    builder.WebHost.ConfigureKestrel(kestrel =>
        kestrel.ConfigureHttpsDefaults(https => https.ServerCertificate = serverCert));
}

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS: origins come from config (Cors:AllowedOrigins), defaulting to the local
// cockpit dev origins. Headers are an explicit allow-list — at minimum the auth
// header + Content-Type.
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[]
    {
        "http://localhost:5173",
        "http://localhost:3001",
        "http://127.0.0.1:3001"
    };
builder.Services.AddCors(options =>
    options.AddPolicy("AllowLocalhost", policy =>
        policy.WithOrigins(corsOrigins)
              .WithMethods("GET", "POST", "OPTIONS")
              .WithHeaders("Authorization", "Content-Type", "Accept"))
);

var app = builder.Build();

// ---------- Binding (loopback by default) ----------
// Secure-by-default: bind to 127.0.0.1 so the bridge is reachable only from the
// local machine unless a non-loopback host is explicitly configured (Host or a
// non-loopback Urls). LAN/remote exposure is opt-in and gated by the startup
// guard below (requires auth + a real ApiKey).
var port = app.Configuration.GetValue<int?>("Port") ?? 5005;
var configuredUrls = app.Configuration.GetValue<string?>("Urls");
var bindHost = app.Configuration.GetValue<string?>("Host");

// Determine the effective binding. Explicit "Urls"/"Host" win; otherwise loopback.
// Collect EVERY URL the app will bind — Kestrel binds all of them, so the
// loopback/exposure decision must consider all, not just the first.
List<string> boundUrls;
if (!string.IsNullOrWhiteSpace(configuredUrls))
{
    // Honor an explicitly configured Urls string as-is.
    app.Urls.Clear();
    boundUrls = configuredUrls
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();
    foreach (var u in boundUrls)
        app.Urls.Add(u);
}
else
{
    var effectiveHost = string.IsNullOrWhiteSpace(bindHost) ? "127.0.0.1" : bindHost.Trim();
    app.Urls.Clear();
    var url = $"http://{effectiveHost}:{port}";
    app.Urls.Add(url);
    boundUrls = new List<string> { url };
}

// Exposed (non-loopback) if ANY bound URL is non-loopback. An empty list (e.g.
// Urls=";;") is treated as NON-loopback so we fail closed.
var isLoopback = boundUrls.Count > 0 && boundUrls.All(u => IsLoopbackHost(ExtractHost(u)));

// Does at least one bound URL terminate TLS itself (https://)?
var hasHttpsBinding = boundUrls.Any(u =>
    Uri.TryCreate(u, UriKind.Absolute, out var uri) &&
    string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase));

// ---------- STARTUP GUARD (fail-closed) ----------
// A non-loopback binding MUST have auth enabled with a non-empty key, otherwise
// the write endpoints (real accounting) are exposed unauthenticated on the LAN.
if (!isLoopback && (!authOpt.Enabled || string.IsNullOrWhiteSpace(authOpt.ApiKey)))
{
    throw new InvalidOperationException(
        "Refusing to start: non-localhost binding requires Auth.Enabled=true and a non-empty Auth:ApiKey. " +
        $"Bound URLs '{string.Join(";", boundUrls)}' include a non-loopback host. " +
        "Provide the key via environment variable or user-secrets, or bind to 127.0.0.1.");
}

// A non-loopback binding MUST terminate TLS, otherwise UseHttpsRedirection()
// silently no-ops and the API key would travel in cleartext while HSTS is
// advertised. Fail closed unless an https:// URL is actually bound.
if (!isLoopback && !hasHttpsBinding)
{
    throw new InvalidOperationException(
        "Refusing to start: non-localhost binding requires an https:// URL (TLS). " +
        "For a TLS-terminating reverse proxy, bind to loopback instead.");
}

// Operability: on loopback, Auth.Enabled with an empty/whitespace key means EVERY
// /api/* call fails 401 (fail-closed). Warn loudly so the cause isn't a silent 401.
if (authOpt.Enabled && string.IsNullOrWhiteSpace(authOpt.ApiKey))
{
    app.Logger.LogWarning(
        "Auth is enabled but no Auth:ApiKey is configured — ALL /api/* requests will return 401. " +
        "Set Auth:ApiKey (env var Auth__ApiKey) or disable Auth for loopback dev.");
}

// The PDF signing secret and the global API key MUST be distinct: a leaked PDF
// download URL carries the HMAC token in its query string; if that secret were the
// API key, the leak would also expose API access. Fail closed on a reused secret.
if (!string.IsNullOrWhiteSpace(pdfOpt.UrlSigningSecret)
    && !string.IsNullOrWhiteSpace(authOpt.ApiKey)
    && string.Equals(pdfOpt.UrlSigningSecret, authOpt.ApiKey, StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "Refusing to start: Pdf:UrlSigningSecret must NOT equal Auth:ApiKey. " +
        "The PDF token travels in the download URL; reusing the API key would leak API " +
        "access whenever a PDF URL leaks. Configure a separate Pdf__UrlSigningSecret.");
}

// Operability: when no PDF signing secret is configured, the download route fails
// closed (every request 403s) and issue/status responses carry pdfUrl=null — which is
// indistinguishable from "this document has no PDF". Warn so a forgotten secret isn't
// a silent 403 later (mirrors the Auth:ApiKey empty-key warning above).
if (string.IsNullOrWhiteSpace(pdfOpt.UrlSigningSecret))
{
    app.Logger.LogWarning(
        "Pdf:UrlSigningSecret is not configured — the PDF download route (GET /api/invoices/{{id}}/pdf) " +
        "will reject every request (403) and issued/status responses will emit pdfUrl=null. " +
        "Set Pdf__UrlSigningSecret (env var) or user-secrets to enable PDF downloads.");
}

// ---------- Middleware pipeline ----------

// Swagger dumps the full API surface; gate it to Development (mirrors the diag
// endpoints) so it isn't exposed unauthenticated in production.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// HTTPS redirection + HSTS only when a non-loopback https binding actually exists
// (the startup guard above guarantees that pairing).
if (!isLoopback && hasHttpsBinding)
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Global exception handler (no internal leakage). Any unhandled exception is logged
// in full server-side under a correlationId; the client gets a generic
// ResponseEnvelope error carrying only that id.
app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var ex = feature?.Error;
    var correlationId = Guid.NewGuid().ToString("N");
    var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("UnhandledException");
    logger.LogError(ex, "Unhandled exception [correlationId={cid}] on {method} {path}",
        correlationId, ctx.Request.Method, ctx.Request.Path);

    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsJsonAsync(new ResponseEnvelope<object>
    {
        Success = false,
        Error = new BridgeError
        {
            Code = "internal",
            Reason = "Wystąpił błąd wewnętrzny. Podaj correlationId podczas zgłoszenia.",
            CorrelationId = correlationId
        }
    });
}));

app.UseCors("AllowLocalhost");

// Auth: protect ALL /api/* with a shared API key. Secure-by-default & fail-closed:
// every /api/* request (any HTTP method) needs a valid key. Only /health is exempt.
// The comparison is constant-time to avoid leaking the key via timing. A
// whitespace-only configured key counts as NOT configured (fail-closed).
//
// The ONLY accepted scheme for the OL integration is `Authorization: Bearer <token>`.
// The bridge strips the `Bearer ` prefix and compares the token to Auth:ApiKey in
// constant time. No other header carries the key.
var apiKeyBytes = string.IsNullOrWhiteSpace(authOpt.ApiKey)
    ? Array.Empty<byte>()
    : Encoding.UTF8.GetBytes(authOpt.ApiKey);
app.Use(async (ctx, next) =>
{
    var isApi = ctx.Request.Path.StartsWithSegments("/api");

    // The PDF download route (GET /api/invoices/{id}/pdf) is opened by the browser as
    // a plain <a href> with NO Authorization header, so the static Bearer cannot guard
    // it. It is instead self-authenticated by a signed token in the query string
    // (validated inside the endpoint). Exempt ONLY this exact route from the Bearer gate.
    // The pattern is pinned to a single numeric id segment (GET-only) so the exemption
    // can NEVER drift looser than the route — a future /api/invoices/.../pdf route does
    // not inherit the bypass.
    var isSignedPdfRoute = HttpMethods.IsGet(ctx.Request.Method) && IsPdfDownloadPath(ctx.Request.Path.Value);

    if (authOpt.Enabled && isApi && !isSignedPdfRoute)
    {
        // Authorization: Bearer <token>. Strip the scheme prefix, then compare the
        // token to the configured key in constant time. This is the ONLY scheme.
        var bearerToken = ExtractBearerToken(ctx.Request.Headers.Authorization.FirstOrDefault());

        if (!IsValidApiKey(bearerToken, apiKeyBytes))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new ResponseEnvelope<object>
            {
                Success = false,
                Error = new BridgeError { Code = "unauthorized", Reason = "Brakujący lub nieprawidłowy nagłówek Authorization: Bearer." }
            });
            return;
        }
    }

    await next();
});

// ---------- Endpoint maps ----------
app.MapHealthEndpoints();
app.MapCustomersEndpoints();
app.MapProductsEndpoints();
app.MapStockEndpoints();
app.MapInvoicesEndpoints();
app.MapBankAccountsEndpoints();
app.MapBranchesEndpoints();
app.MapCashRegistersEndpoints();
app.MapAuditEndpoints();

// Diagnostics introspection endpoints register ONLY when Diagnostics:Enabled AND
// the host is in Development. They dump BO shapes / DB rows, so they must never be
// reachable in production. They still sit behind the /api auth middleware.
if (diagOpt.Enabled && app.Environment.IsDevelopment())
{
    app.MapDiagnosticsEndpoints();
}

app.Run();

// ---------- Local middleware helpers (composition root) ----------

// Constant-time API-key check. Null/empty provided value or an empty configured
// key -> unauthorized (fail-closed). FixedTimeEquals is timing-safe and returns
// false for any length mismatch without leaking the secret content via timing.
static bool IsValidApiKey(string? provided, byte[] expectedBytes)
{
    if (string.IsNullOrEmpty(provided) || expectedBytes.Length == 0) return false;
    var providedBytes = Encoding.UTF8.GetBytes(provided);
    return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
}

// Extract the token from an "Authorization: Bearer <token>" header value. Returns
// null when the header is absent or doesn't use the Bearer scheme (case-insensitive
// scheme match per RFC 6750). The token is returned verbatim (validated downstream
// by the constant-time comparison); nothing here is logged.
static string? ExtractBearerToken(string? authorizationHeader)
{
    if (string.IsNullOrWhiteSpace(authorizationHeader)) return null;
    const string prefix = "Bearer ";
    if (!authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
    var token = authorizationHeader.Substring(prefix.Length).Trim();
    return token.Length == 0 ? null : token;
}

// Exact-match the PDF download route /api/invoices/{digits}/pdf (single numeric id
// segment). Pinned so the Bearer exemption matches ONLY this route — not any path
// under /api/invoices that happens to end in /pdf. Token auth is enforced inside the
// handler regardless; this just keeps the exemption from drifting wider than the route.
static bool IsPdfDownloadPath(string? path)
{
    if (string.IsNullOrEmpty(path)) return false;
    // Shared with PdfUrlSigner.BuildUrl + the MapGet registration so the exemption,
    // the emitted URL, and the route can never drift apart.
    const string prefix = PdfUrlSigner.RoutePrefix;
    const string suffix = PdfUrlSigner.RouteSuffix;
    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
    if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
    var idSegment = path.Substring(prefix.Length, path.Length - prefix.Length - suffix.Length);
    return idSegment.Length > 0 && idSegment.All(char.IsAsciiDigit);
}

// Pull the host out of a single "http://host:port" url (best-effort; used per
// bound URL for the loopback decision).
static string ExtractHost(string url)
{
    var trimmed = url.Trim();
    return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ? uri.Host : trimmed;
}

// True for 127.0.0.0/8, ::1, and "localhost". Anything else counts as exposed and
// triggers the auth/HTTPS requirements.
static bool IsLoopbackHost(string host)
{
    if (string.IsNullOrWhiteSpace(host)) return false;
    if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
    if (System.Net.IPAddress.TryParse(host, out var ip))
        return System.Net.IPAddress.IsLoopback(ip);
    return false;
}
