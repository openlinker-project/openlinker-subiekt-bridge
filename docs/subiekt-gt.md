# Subiekt GT bridge (`bridge-gt/`)

## What this is

`bridge-gt/` is a second, independent bridge in this repository. It lets OpenLinker talk to
**Subiekt GT**, a different InsERT product from the Subiekt nexo that `bridge/` was built for.
The two share a vendor and a family name, but not an integration surface, so this bridge shares
no code with `bridge/`. Treat it as a separate application that happens to live in the same
repository because both exist to plug an InsERT product into OpenLinker.

This is a **spike shim**, not a finished production design - the source says so in its own
header comment. It currently speaks a read-only subset of the WooCommerce REST v3 dialect for
products/orders/customers (so OpenLinker's existing WooCommerce `ProductMaster` /
`InventoryMaster` adapters can read Subiekt GT with no change on the OpenLinker side) plus a
small OpenLinker-native `/api/*` surface for orders, invoicing and fiscalization. Expect rough
edges: some endpoints are unverified against a live fiscal printer, and there is no test suite.

## How it differs from the nexo bridge (`bridge/`)

| | `bridge/` (Subiekt nexo) | `bridge-gt/` (Subiekt GT) |
|---|---|---|
| Integration surface | InsERT **Sfera SDK** - a managed .NET API (`InsERT.Moria.*`) | Classic **COM automation** server, ProgID `InsERT.GT`, driven through `dynamic` COM interop |
| Project shape | Layered solution (`Api` / `Application` / `Domain` / `Infrastructure.Sfera` / `Infrastructure.Sql` / `Infrastructure.Persistence`) | A single ASP.NET Core project, `GtBridge.csproj` |
| Database reads | Through Sfera's own object model where it has a facade, direct SQL only where it doesn't | Direct SQL against Subiekt GT's own schema for everything a facade doesn't cover: `tw__Towar` (products), `dok__Dokument` (documents, including `dok_Pozycja` lines), `kh__Kontrahent` (contractors), `adr__Ewid` (addresses), `sl_GrupaTw` (product groups), `sl_Panstwo` (countries), `tw_Stan` (stock), plus smaller tables such as `tw_Cena`, `tw_KodKreskowy`, `sl_StawkaVAT` and `rb__RachBankowy` |
| Threading | Sfera's own session model | One dedicated **STA** thread and a work queue - COM is apartment-bound, so every call, from every request, is marshalled onto that one thread |

A source-code naming quirk worth knowing if you read the code: the file that drives the COM
session is called `Sfera.cs` and its header comment says "Sfera GT writer". That name is
historical. The file does **not** use the Sfera SDK - it opens `Type.GetTypeFromProgID("InsERT.GT", true)`
and drives the resulting COM object with `dynamic`. Do not read the filename as a claim that this
bridge shares a mechanism with `bridge/`.

**Compatibility with Subiekt nexo is unverified and out of scope here.** This bridge was written
and tested against Subiekt GT only. Whether any of it (the SQL schema, the COM automation model,
anything else) also holds for nexo is a separate exercise for later - do not assume it, and
nothing in this bridge or this document claims it.

## Prerequisites

- **Windows** - the bridge depends on COM automation against a running `InsERT.GT` object, which
  has no Linux or cross-platform equivalent.
- **Subiekt GT** installed and licensed, with **Sfera GT** enabled (GT's own add-on that exposes
  the `InsERT.GT` automation object - not the Sfera SDK `bridge/` uses, despite the shared name).
  GT >= 1.12 is needed for `SuDokumentyManager.DodajPAf()` (paragon fiskalny); GT >= 1.23 for
  `RejestrujNaUF` (fiscal registration).
- **.NET 8 SDK** on the Windows machine.
- SQL Server access to the Subiekt GT database (integrated security by default; SQL
  authentication is supported as a fallback - see Configuration).
- A dedicated Subiekt operator account with the minimum privileges the bridge needs (read
  products/documents/contractors, create documents, register fiscal transactions) - not the
  `Szef` (admin) account for anything beyond a demo/sandbox install. `Szef` is only the
  **compiled-in default** because that is what this sandbox happens to run as; it is not a
  recommendation.

## Build and run

All commands are plain Windows PowerShell.

```powershell
cd bridge-gt
copy appsettings.example.json appsettings.json
notepad appsettings.json
dotnet run -c Release
```

A healthy bridge logs its resolved configuration on one line (`BridgeConfig.Describe()` -
secrets are deliberately never echoed) and then attaches to Subiekt GT in the background before
the first request arrives (see "Cold attach" below).

Smoke test:

```powershell
Invoke-RestMethod http://127.0.0.1:5056/health
```

(`5056` is the plain-HTTP port's default - see Configuration. `/health` is deliberately
unauthenticated, alongside `/gt-image`, so a load balancer or monitoring probe needs no
credential.)

## Configuration

Resolution order, highest first:

1. an environment variable, `OL_BRIDGE_<KEY_UPPER_SNAKE_CASE>` (e.g. `SqlServer` ->
   `OL_BRIDGE_SQL_SERVER`, `SferaOperator` -> `OL_BRIDGE_SFERA_OPERATOR`)
2. `appsettings.json`, read once at startup from the directory the executable runs in (not
   reloaded - restart the bridge after editing it)
3. a compiled-in default

Every key is optional. A bridge with no `appsettings.json` and no environment variables set at
all runs with every value at its rung-3 default - which is exactly this sandbox's own
configuration, since that is what the defaults were copied from. `appsettings.json` itself is
optional and a malformed one is logged and ignored rather than treated as fatal, so a typo in it
cannot take the bridge down in a way nobody on-site can fix.

| Key | Purpose | Has a non-empty default? |
|---|---|---|
| `SqlServer` | SQL Server instance name | Yes (`localhost\INSERTNEXO`, InsERT's own installer default) |
| `SqlDatabase` | Database name | Yes (this sandbox's own) |
| `SqlConnectionString` | Full ADO.NET connection string; when set, overrides `SqlServer`/`SqlDatabase` entirely - use this for SQL authentication instead of the integrated-security string the other two compose | No |
| `SferaOperator` | Operator account the COM session logs in as | Yes (`Szef`) |
| `SferaPassword` | That operator's password | **No** - blank is a legitimate value on a demo install with no operator password set |
| `ApiUser` / `ApiPassword` | HTTP Basic credentials guarding the WooCommerce-dialect shim routes | **No** - unset leaves those routes closed |
| `InvoiceToken` | Bearer / `x-bridge-token` value guarding the `/api/*` routes | **No** - unset leaves those routes closed |
| `CertificatePath` / `CertificatePassword` | HTTPS certificate for the Kestrel HTTPS listener | **No** - unset means the HTTPS listener is simply not opened |
| `HttpsPort` | HTTPS listener port | Yes (`5055`) |
| `HttpPort` | Plain-HTTP listener port (images only - see below) | Yes (`5056`) |
| `PublicBase` | Externally-resolvable base URL images are served under | Yes (this sandbox's own) |

Two things about the three auth keys are load-bearing, not incidental: they default to the empty
string, and the middleware checks "is this configured at all" *before* comparing credentials -
never after. With a blank default and an after-the-fact comparison, an unconfigured bridge would
accept a request that also sent a blank credential; checking configuration first is what keeps
"nobody set these" meaning "these routes are closed" rather than "these routes accept anything".
The check is ordered so that this is true rather than merely intended: with a blank token the
header comparison would be `"" == ""`, which every request without a header satisfies, so the
"is this configured" test runs BEFORE the comparison.

The certificate keys have no default for a second reason as well: a path compiled into a binary
names one machine's file layout and cannot be right anywhere else, and its passphrase would be a
plaintext password in source. Unset does not stop the bridge - only the HTTPS listener is skipped,
and the plain-HTTP port still serves, which is the port OpenLinker reaches the bridge on.

`PublicBase` needs a real, externally-resolvable host name or IP, never `localhost` - product
images are fetched by OpenLinker and, through it, by marketplaces, from outside this machine, and
a `localhost` value fails silently as `IMAGE_DOWNLOAD_FAILED` on the marketplace side with nothing
pointing back here.

## HTTP surface

Two authentication zones, wired as separate middleware in `Program.cs`:

- `/health` and `/gt-image/*` - no authentication.
- Every other `/wp-json/wc/v3/*` route - HTTP Basic, guarded by `ApiUser`/`ApiPassword`.
- Every `/api/*` route - Bearer or `x-bridge-token`, guarded by `InvoiceToken`.

### WooCommerce-dialect read shim (`/wp-json/wc/v3/*`)

Read-only (plus the writes WooCommerce's own dialect needs for order/customer round-trips), so
that OpenLinker's existing WooCommerce adapters can read Subiekt GT as if it were a WooCommerce
store, with no OpenLinker-side code change:

- `GET /products`, `GET /products/{id}`, `GET /products/{id}/variations`,
  `GET /products/{parentId}/variations/{id}`, `GET /products/categories`
- `GET /orders`, `GET /orders/{id}`, `PUT /orders/{id}`, `POST /orders`
- `GET /customers`, `GET /customers/{id}`, `POST /customers`, `PUT /customers/{id}`
- `GET /settings/general`, `GET /taxes`, `GET /system_status`

### OpenLinker-native routes (`/api/*`)

- `GET /api/products`, `GET /api/products/search`, `GET /api/products/categories`,
  `GET /api/products/{symbol}`, `POST /api/products`, `PUT /api/products/{symbol}`
- `POST /api/orders`, `GET /api/orders/feed`, `GET /api/orders/{id}`,
  `PUT /api/orders/{id}/shipping`
- `GET /api/inventory/{towarSymbol}/stock`, `POST /api/inventory/adjust`
- `POST /api/invoices`, `POST /api/invoices/{origId}/corrections`,
  `GET /api/invoices/{id}/status`, `GET /api/invoices/locate`
- `POST /api/customers/upsert`
- `GET /api/bank-accounts`, `PUT /api/bank-accounts/{id}/default`
- `GET /api/cash-registers`
- `POST /api/fiscalize`

### Image serving

- `GET /gt-image/{towarId}` - served over the plain-HTTP port, deliberately, because a marketplace
  fetching a product image cannot be assumed to trust this machine's TLS certificate.

## Operating notes

- **COM is apartment-bound.** Every Sfera-GT (COM) call in this bridge, from every concurrent
  HTTP request, is marshalled onto one dedicated STA thread with a work queue. This is not a
  performance shortcut that could be relaxed later - COM automation objects created on an STA
  thread may only be called from that same thread, so there genuinely is one lane, however many
  requests are in flight.
- **A cold attach is slow - about 81 seconds, measured.** That is well past OpenLinker's own
  30-second HTTP timeout on a sync call, so the bridge attaches to Subiekt GT in the background
  at startup (`Sfera.Warmup()`) rather than waiting for the first real request to trigger it. On
  a fresh boot, give the bridge roughly a minute and a half before expecting a request to
  complete promptly; before that window, OpenLinker may report a sync as failed even though the
  underlying write actually completed once the attach finished.
- **An existing kontrahent (contractor) is never re-saved.** Subiekt GT raises a modal
  confirmation dialog when an existing kontrahent record is written again, and a COM call that
  triggers a modal dialog blocks forever - .NET Core has no way to cancel it. So the bridge always
  resolves an existing kontrahent first and returns it untouched; it only creates a new one when
  none exists. The same rule applies to the order and invoicing paths independently, since both
  need to create or reuse a kontrahent. A small best-effort `DialogWatcher` background thread
  additionally dismisses a short, text-matched list of known-harmless dialogs, as a backstop for
  the class of dialog nobody has hit yet - it is not the primary defence, the never-resave rule
  is.
