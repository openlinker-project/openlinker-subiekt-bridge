# OpenLinker Subiekt Bridge

A small .NET 8 service that lets [OpenLinker](https://github.com/openlinker-project/openlinker)
issue invoices in **Subiekt nexo** via the **InsERT Sfera SDK**.

## Why this exists

OpenLinker's Subiekt nexo invoicing adapter (`@openlinker/integrations-subiekt`) never talks
to Subiekt directly. Sfera is a Windows-only SDK with no Linux or cross-platform client, while
OpenLinker's core API runs on Linux/containers. The bridge is the piece that lives on the
Windows machine where Subiekt nexo is installed, exposes a small HTTPS API, and translates
OpenLinker's neutral invoice commands into Sfera SDK business operations:

```
OpenLinker  →  HTTPS + Bearer  →  Subiekt Bridge  →  Sfera SDK  →  Subiekt nexo
```

Document type is driven by the buyer's tax ID: an order **with** a NIP becomes a **faktura**
(FS); **without** one it becomes a **paragon** (PA).

This repository ships the bridge as a **plain .NET console/ASP.NET Core app run via
`dotnet run`** — it is intentionally **not** packaged as a compiled `.exe`. (An older,
self-contained-exe packaging approach was tried and abandoned; running from source with
`dotnet run` is the supported path.)

## Prerequisites

- **Windows** — the bridge depends on the Sfera SDK, which is Windows-only.
- **Subiekt nexo PRO** with the **Sfera** add-on installed and licensed (the demo/test
  database ships with Sfera built in; a purchased production Subiekt nexo does **not** —
  it's a separate paid add-on. Confirm/purchase the Sfera license before going to production).
- **.NET 8 SDK** installed on the Windows machine.
- SQL Server access to the Subiekt nexo database.
- A dedicated Subiekt operator account with **minimal privileges** (issue FS/PA, corrections,
  create contractors/products) — never the `Szef` (admin) account.

## Project layout

```
bridge/
├── Subiekt.Bridge.Api/                  # ASP.NET Core host — endpoints, appsettings, auth, TLS
├── Subiekt.Bridge.Application/          # Use cases / commands (e.g. IssueInvoiceHandler)
├── Subiekt.Bridge.Domain/               # Domain entities and ports
├── Subiekt.Bridge.Infrastructure.Sfera/ # Sfera SDK facades (IPodmioty, IDokumentySprzedazy, ...)
├── Subiekt.Bridge.Infrastructure.Sql/   # Direct SQL reads where Sfera has no facade
├── Subiekt.Bridge.Infrastructure.Persistence/  # Idempotency store / audit log
├── tools/                               # Diagnostics (e.g. SferaInspect)
└── tests/                               # Unit tests
docs/                                    # Setup, deployment, architecture, API reference
cockpit/                                 # React test harness UI for exercising bridge endpoints manually
```

## Running the bridge

All commands below are **plain Windows PowerShell** — not WSL, not bash.

### 1. Configure `appsettings.json`

```powershell
cd bridge\Subiekt.Bridge.Api
copy appsettings.example.json appsettings.json
notepad appsettings.json
```

Fill in the `Sfera` section with your nexo deployment's paths and database:

```jsonc
"Sfera": {
  "BinariesDir": "C:\\Users\\<user>\\AppData\\Local\\InsERT\\Deployments\\Nexo\\<Deployment>\\Binaries",
  "ConfigDir":   "C:\\Users\\<user>\\AppData\\Local\\InsERT\\Deployments\\Nexo\\<Deployment>\\Config",
  "TempDir":     "C:\\Users\\<user>\\AppData\\Local\\InsERT\\Deployments\\Nexo\\<Deployment>\\Work",
  "DeploymentName": "Nexo",
  "SqlServer": "localhost\\INSERTNEXO",
  "SqlDatabase": "YourDatabase",
  "SqlUseWindowsAuth": true,
  "SqlEncrypt": true,
  "NexoUser": "operator_min_uprawnienia"
}
```

Leave all secret fields (`Auth.ApiKey`, `Sfera.SqlPassword`, `Sfera.NexoPassword`,
`Tls.CertPassword`) **empty in the file** — they're supplied via environment variables below.
Never commit secrets.

### 2. Set secrets via environment variables

```powershell
$env:Sfera__NexoPassword = "<nexo-operator-password>"
$env:Sfera__SqlPassword  = "<sql-password>"          # only if not using Windows auth
$env:Auth__ApiKey        = "<bearer-token-openlinker-uses>"
```

`Auth__ApiKey` is the bearer token OpenLinker's connection config (`bridgeToken`) must match.

### 3. (Optional) TLS for non-loopback access

By default the bridge listens on `127.0.0.1` only. To expose it on the LAN so OpenLinker
(running elsewhere, e.g. in a container or on another host) can reach it, you need HTTPS:

```powershell
# Dev/self-signed certificate (do not use in production)
dotnet dev-certs https -ep cert.pfx -p <cert-password>

$env:Tls__CertPassword = "<cert-password>"
$env:ASPNETCORE_URLS    = "https://0.0.0.0:5005"
```

In production, use a real certificate from a trusted CA, or terminate TLS on a reverse proxy
and keep the bridge listening on loopback. Non-loopback listening is fail-closed: it requires
`Auth.Enabled=true` with a non-empty `ApiKey` and at least one `https://` URL, or the bridge
refuses to start. See `docs/DEPLOYMENT.md` for the full production checklist (firewall rules,
TLS options, least-privilege account setup).

### 4. Run

```powershell
dotnet run -c Release --project bridge\Subiekt.Bridge.Api
```

A healthy bridge logs `Now listening on: ...` and a successful Sfera session connect.

### 5. Smoke test

```powershell
Invoke-RestMethod http://127.0.0.1:5005/health
```

Expect something like:

```json
{ "status": "ok", "sferaSession": "valid", "subiekt": "reachable" }
```

(If you configured HTTPS with a self-signed cert, use `Invoke-RestMethod -SkipCertificateCheck`
or trust the certificate first.)

## Further documentation

- [`docs/SETUP.md`](docs/SETUP.md) — detailed setup walkthrough
- [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md) — test → production deployment checklist
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — design decisions
- [`docs/API_ENDPOINTS.md`](docs/API_ENDPOINTS.md) — bridge endpoint reference
- [`docs/SUBIEKT_OPENLINKER_INTEGRATION.md`](docs/SUBIEKT_OPENLINKER_INTEGRATION.md) — how this
  bridge plugs into OpenLinker's `@openlinker/integrations-subiekt` adapter

## Cockpit (manual test harness)

`cockpit/` is a small React + Vite UI for exercising bridge endpoints by hand during
development (connect, list products/contractors, issue invoices, check KSeF status). It is
not required to run the bridge in production — see `cockpit/README.md`-equivalent notes in
`docs/SETUP.md` if you want to use it.
