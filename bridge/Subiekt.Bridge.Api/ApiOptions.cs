namespace SferaApi;

// Composition-root (Api) options that are NOT part of the Sfera session config.
// SferaOptions itself moved to Subiekt.Bridge.Infrastructure.Sfera in Faza 3B.

// Bound from the top-level "Auth" section.
public class AuthOptions
{
    // Secure-by-default: auth is ON. All /api/* calls require a valid token passed
    // as `Authorization: Bearer <token>` (the ONLY accepted scheme). Only set false
    // for a strictly loopback-bound dev setup — a non-loopback binding with auth off
    // refuses to start (see Program.cs).
    public bool Enabled { get; set; } = true;
    public string ApiKey { get; set; } = "";
}

// Bound from the top-level "Diagnostics" section. The /api/diag/* introspection
// endpoints dump internals (BO shapes, DB rows) and must stay off by default;
// they only register when Enabled AND the host is in Development.
public class DiagnosticsOptions
{
    public bool Enabled { get; set; } = false;
}
