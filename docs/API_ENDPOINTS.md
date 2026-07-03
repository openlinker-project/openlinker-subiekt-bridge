# Bridge API Endpoints Reference

Base URL:
- Lokalny dev (loopback): `http://localhost:5005`
- OpenLinker → most przez sieć (PRODUKCJA): `https://<host>:5005` — `bridgeBaseUrl`
  w OL MUSI być adresem **HTTPS**. Most odmawia startu na nasłuchu nie-loopback bez
  TLS, więc komunikacja sieciowa zawsze idzie po HTTPS. Certyfikat: prawdziwy z CA
  (prod) lub terminacja TLS na reverse-proxy. Szczegóły: `docs/DEPLOYMENT.md` §1a.

## Health & Connection

### GET /health
Health check.

**Response:**
```json
{
  "status": "ok",
  "time": "2026-06-18T13:45:00Z"
}
```

### POST /api/session/connect
Establish Sfera session. Idempotent — safe to call multiple times.

**Request:** (empty body)

**Response (success):**
```json
{
  "connected": true
}
```

**Response (error):**
```json
{
  "detail": "Invalid Sfera credentials",
  "status": 500
}
```

### GET /api/session/status
Check if Sfera session is connected.

**Response:**
```json
{
  "connected": true
}
```

---

## Read Operations (SQL-based)

### GET /api/products
List products.

**Query params:**
- `limit` (optional, default 50): number of records to return

**Response:**
```json
{
  "limit": 50,
  "count": 3,
  "items": [
    {
      "Asortyment_Id": 1,
      "Symbol": "SKU001",
      "Nazwa": "Widget A",
      "Jednostka": "szt",
      "CenaWyd": 99.99
    }
  ]
}
```

### GET /api/products/{symbol}
Get single product by symbol.

**Response (found):**
```json
{
  "found": true,
  "item": {
    "Asortyment_Id": 1,
    "Symbol": "SKU001",
    "Nazwa": "Widget A",
    "Jednostka": "szt",
    "CenaWyd": 99.99
  }
}
```

**Response (not found):**
```json
{
  "found": false
}
```

### GET /api/batches/{symbol}
List batches/delivery codes for a product.

**Query params:**
- `magazyn` (optional): warehouse symbol to filter

**Response:**
```json
{
  "items": [
    {
      "Numer": "PART001",
      "DataProdukcji": "2026-01-01",
      "DataWaznosci": "2027-01-01"
    }
  ]
}
```

### GET /api/customers
List contractors/customers.

**Query params:**
- `limit` (optional, default 50): number of records

**Response:**
```json
{
  "count": 2,
  "items": [
    {
      "Id": 1,
      "NazwaSkrocona": "Company A",
      "NIP": "1234567890",
      "NIPSformatowany": "123-456-78-90",
      "Telefon": "+48123456789",
      "Sygnatura": "SYG001",
      "Aktywny": true,
      "Kontrahent": 1
    }
  ]
}
```

### GET /api/customers/{id}
Get single contractor by ID.

**Response (found):**
```json
{
  "found": true,
  "item": { ... }
}
```

### GET /api/warehouses
List warehouses.

**Response:**
```json
{
  "items": [
    {
      "Id": 1,
      "Symbol": "GW",
      "Nazwa": "Główny Magazyn",
      "Opis": "Main warehouse"
    }
  ]
}
```

### GET /api/stock
List stock levels.

**Query params:**
- `magazyn` (optional): warehouse symbol
- `symbol` (optional): product symbol
- `limit` (optional, default 200): number of records

**Response:**
```json
{
  "items": [
    {
      "Asortyment_Id": 1,
      "Symbol": "SKU001",
      "Magazyn_Id": 1,
      "Magazyn_Symbol": "GW",
      "IloscDostepna": 100,
      "IloscZarezerwowana": 10
    }
  ]
}
```

### GET /api/stock/{symbol}
Get stock levels for a specific product across all warehouses.

**Response:**
```json
{
  "items": [ ... ]
}
```

---

## Write Operations (Sfera-based)

### POST /api/customers/upsert
Create or update a contractor.

**Request:**
```json
{
  "nazwaSkrocona": "New Company",
  "nip": "1234567890",
  "telefon": "+48123456789",
  "aktywny": true,
  "typ": "firma"
}
```

**Fields:**
- `nazwaSkrocona` (required): Short name
- `nip` (optional): Tax ID number
- `telefon` (optional): Phone number
- `aktywny` (optional, default true): Is active
- `typ` (optional, default "firma"): "firma" (company) or "osoba" (individual)

**Response (success):**
```json
{
  "success": true,
  "data": {
    "id": 42,
    "nazwaSkrocona": "New Company",
    "nip": "1234567890"
  }
}
```

**Response (error):**
```json
{
  "success": false,
  "error": "NIP already exists"
}
```

---

### POST /api/invoices
Create invoice or paragon.

**Request:**
```json
{
  "kontrahentId": 1,
  "towary": [
    {
      "symbol": "SKU001",
      "ilosc": 5,
      "cena": 99.99
    },
    {
      "symbol": "SKU002",
      "ilosc": 2,
      "cena": 49.50
    }
  ],
  "dokTyp": "faktura",
  "dataSprz": "2026-06-18"
}
```

**Fields:**
- `kontrahentId` (required): Contractor ID
- `towary` (required): Array of line items
  - `symbol` (required): Product symbol
  - `ilosc` (required): Quantity
  - `cena` (required): Unit price
- `dokTyp` (optional, default "faktura"): "faktura" or "paragon"
- `dataSprz` (optional, default today): Sale date (ISO format)

**Response (success):**
```json
{
  "success": true,
  "data": {
    "invoiceId": "abc12345",
    "invoiceNumber": "FV/20260618/abc12345",
    "kontrahentId": 1,
    "iloscPozycji": 2,
    "dokTyp": "faktura",
    "status": "draft"
  }
}
```

**Response (error):**
```json
{
  "success": false,
  "error": "Kontrahent ID 999 not found"
}
```

---

### GET /api/invoices/{id}/status
Get invoice/paragon status and KSeF submission state.

**Response (success):**
```json
{
  "success": true,
  "data": {
    "invoiceId": "abc12345",
    "status": "draft",
    "ksef": {
      "submitted": false,
      "status": "not_submitted"
    },
    "createdAt": "2026-06-18T13:45:00Z"
  }
}
```

**Response (error):**
```json
{
  "success": false,
  "error": "Invoice not found"
}
```

---

## Error Responses (structured — POC hardening)

Write endpoints now return the OL contract's two error classes in a structured envelope:

```json
{
  "success": false,
  "error": { "code": "unreachable", "reason": "..." }
}
```

| code | HTTP | meaning | OL action |
|------|------|---------|-----------|
| `unreachable` | 503 | bridge can't reach Subiekt/SQL or the Sfera session is gone (infra/transient) | retry |
| `rejected` | 422 | Subiekt/Sfera looked at the request and refused (validation, missing product, bad NIP, `Zapisz=false`) | don't retry blindly; fix the request |
| `unauthorized` | 401 | missing/invalid API key (only when `Auth:Enabled=true`) | add `Authorization: Bearer <token>` |

---

## POC hardening — what changed vs the original sketch

### `GET /health` — three independent signals
```json
{ "status": "ok", "bridge": "up", "sferaSession": "valid", "subiekt": "reachable", "subiektError": null, "time": "..." }
```
`status` is `ok` / `sfera_session_invalid` / `subiekt_unreachable`. Lets OL's connection-test tell "bridge down" from "session bad" from "SQL/Subiekt down".

### Auto-connect & session
- Bridge connects to Sfera automatically on startup (`Sfera:AutoConnect`, default `true`). Manual `POST /api/session/connect` remains as a fallback.
- Business calls run through `EnsureConnected()` which re-logs-in a stale session before the operation.

### Auth (optional)
Top-level `Auth` section in `appsettings.json`:
```json
"Auth": { "Enabled": false, "ApiKey": "" }
```
When `Enabled=true` and `ApiKey` is set, every `/api/*` request (any method) must carry the key. Off by default so the cockpit/demo works. `/health` stays anonymous.

**The ONLY accepted scheme is `Authorization: Bearer <token>`.** The bridge strips the `Bearer ` prefix and compares the token to `Auth:ApiKey` in constant time. No other header (e.g. the former `X-Api-Key`) is accepted.

> **HTTPS required off-loopback.** A non-loopback binding refuses to start without `Auth:Enabled=true`, a non-empty `Auth:ApiKey`, and an `https://` URL (TLS) — the bearer token must never travel in cleartext over the LAN. On `127.0.0.1` plain HTTP is fine for dev.

### `POST /api/invoices` — contract-shaped request & response
Current request (symbol-based lines — the working Sfera path):
```json
{
  "kontrahentId": 1,
  "documentType": "FV",
  "currency": "PLN",
  "issueDate": "2026-06-19",
  "orderId": "ORD-123",
  "idempotencyKey": "ORD-123:issue",
  "lines": [ { "towarSymbol": "SKU001", "ilosc": 2, "cenaBrutto": 123.00, "stawkaVAT": "23" } ]
}
```
Response adds OL-shaped fields alongside the legacy ones:
```json
{
  "success": true,
  "data": {
    "providerInvoiceId": 42,
    "providerInvoiceNumber": "FS 7/2026",
    "documentType": "FV",
    "currency": "PLN",
    "regulatoryStatus": "pending",
    "pdfUrl": null,
    "state": "issued",
    "idempotent": false,
    "orderId": "ORD-123"
  }
}
```

- **`documentType`** well-known values (string, provider-native; the bridge only *executes* the passed type — the NIP→type decision stays on the OL side):
  - `FV` → faktura sprzedaży (`UtworzFaktureSprzedazy`)
  - `PA` → paragon (`UtworzParagon`)
- **Gross pricing**: `cenaBrutto` (OL `unitPriceGross`) is applied to the line (`CenaRecznieEdytowana` + `Cena.Brutto*`), then `Przelicz` recomputes the netto/VAT split.
- **VAT**: `stawkaVAT` (`"23"/"8"/"5"/"0"/"zw"/"np"`) maps to `StawkiVat.Id`; falls back to the product default if not found.
- **Idempotency**: when `idempotencyKey` is supplied, a repeated call returns the same `providerInvoiceId` (`idempotent: true`) instead of a duplicate fiscal document. Map persisted to `idempotency-store.json`.
- **`pdfUrl`**: not exposed by Sfera in this POC → always `null` (documented).
- **Currency**: only `PLN` is exercised in v1; other currencies are echoed but not specially handled.

### `GET /api/bank-accounts` — seller bank accounts (issue #1, multi-Podmiot in issue #3)
Lists ACTIVE bank accounts from Subiekt's "Rachunki bankowe" configuration across **every** seller (MojaFirma) Podmiot — an install may have more than one payer/branch, each with its own accounts (issue #3; previously only the first Podmiot's accounts were returned). Read-only, served from the separate SQL connection (never contends with the Sfera write session).

```json
{
  "success": true,
  "data": {
    "count": 2,
    "accounts": [
      { "id": 100004, "name": "Rachunek podstawowy", "number": "00 10101010 1111 1111 1111 1111",
        "bankNumber": "", "description": "", "currency": "PLN", "isVatAccount": false, "isDefault": true,
        "ownerPodmiotId": 1, "ownerName": "Moja Firma Sp. z o.o." },
      { "id": 100007, "name": "Rachunek on-line - testowy", "number": "38 2490 0005 7898 4745 0552 5035",
        "bankNumber": "2490", "description": "", "currency": "PLN", "isVatAccount": false, "isDefault": false,
        "ownerPodmiotId": 1, "ownerName": "Moja Firma Sp. z o.o." }
    ]
  }
}
```

- `isDefault` mirrors the Subiekt UI's "Podstawowy" column (the owner's primary-account back-reference), not the per-currency flag. It is per-owning-Podmiot, not global.
- `ownerPodmiotId` / `ownerName` identify which seller Podmiot the account belongs to (issue #3) — use this to group accounts by payer/branch until an explicit selector exists on the invoice-issuance contract (tracked as open work on issue #3).
- `id` is the value to pass as `bankAccountId` on `POST /api/invoices` and to `PUT /api/bank-accounts/{id}/default`.
- **Ordering (issue #3)**: results are grouped by owner Podmiot first (`Wlasciciel_Id`), then by default-status within that owner (default account first), then by `id`. Callers that previously relied on "the default account is always first overall" on a single-Podmiot install should instead group by `ownerPodmiotId` — on multi-payer installs the response is no longer a flat "default-first" list.
- **Known gap — no automated DB-backed test**: `SqlBankAccountsReader` and the multi-Podmiot enumeration logic have no automated SQL-Server-backed test in this repo (no test harness exists yet for the Sfera SQL reader layer). This was verified only via a live manual probe (`BankAccountProbe`) against a real Subiekt install, so correctness here is tracked debt rather than covered by CI.

### `PUT /api/bank-accounts/{id}/default` — set the seller default account (issue #1)
Makes `{id}` the default ("Podstawowy") account **of the Podmiot that owns it** — each seller Podmiot keeps its own default, so on a multi-payer install this does not set one global default (issue #3). Runs as a Podmiot business-object save on the Sfera write queue; the owning Podmiot's previous default clears automatically. Idempotent — selecting the current default succeeds without a write. Unknown / inactive / non-seller accounts return the `rejected` envelope (HTTP 422).

```json
{ "success": true, "data": { "bankAccountId": 100007, "isDefault": true } }
```

### Payment selection on `POST /api/invoices` (issue #1)
Two ADDITIVE, optional fields select the payment explicitly; both absent keeps today's provider-default payments (no regression):

```json
{ "paymentMethod": "transfer", "bankAccountId": 100007 }
```

STRICT semantics (any other combination is rejected, HTTP 422 `validation`):

| paymentMethod | bankAccountId | result |
|---|---|---|
| absent | absent | provider defaults (today's behavior) |
| `"cash"` | absent | immediate payment via the configured cash form |
| `"transfer"` | present | deferred payment via the configured transfer form + the chosen account stamped on the document (`RachunekBankowyMojejFirmy`) |
| `"transfer"` | absent | 422 — transfer requires `bankAccountId` |
| `"cash"` | present | 422 — cash must not carry an account |
| absent | present | 422 — account requires explicit `"transfer"` |
| any | any (on `documentType: "PA"`) | 422 — not supported for paragony |

- Vocabulary errors (`paymentMethod` outside `cash`/`transfer`, non-positive `bankAccountId`) are 400 `bad_request` (shape validation).
- The account must exist, be ACTIVE, belong to **a** seller (MojaFirma) Podmiot and match the document currency — otherwise 422 `rejected`.
- **Multi-payer caveat (issue #3)**: on installs with more than one seller Podmiot the bridge does NOT yet validate that the account's owning Podmiot matches the Podmiot the document is issued under — the caller must ensure `bankAccountId` belongs to the intended payer (pick from `GET /api/bank-accounts` by `ownerPodmiotId`). The bridge logs a warning on every such issuance until the Oddział/Płatnik selector lands (open work on issue #3).
- The cash/transfer → `FormaPlatnosci` mapping is configurable: `Sfera:CashPaymentFormName` (default `gotówka`) and `Sfera:TransferPaymentFormName` (default `przelew`), matched case-insensitively among active payment forms.

### `GET /api/branches` — seller branches (Oddziały), INFORMATIONAL ONLY (issue #5)
Lists every Oddzial (`JednostkaOrganizacyjna`), independent of the seller Podmiot axis from issue #3 — a single-payer install can still have multiple branches. Read-only, served from the separate SQL connection.

```json
{ "success": true, "data": { "count": 2, "branches": [
  { "id": 100001, "name": "Pachnidło" },
  { "id": 100002, "name": "Centrum Handlowe" }
] } }
```

**This endpoint is informational only — its `id` is NOT accepted anywhere on `POST /api/invoices`.** Live investigation (`docs/spikes/podmioty-oddzial-stanowisko-probe-findings.md` §8) proved a Subiekt document's operative branch comes from the **logged-in session's business context** (`IKontekstBiznesowy` — read-only, fixed per authenticated user), never from a per-document field. Neither patching the document after creation nor supplying the branch via its creation parameters overrides it; both were tried live against a real Sfera connection and both failed identically. Routing an invoice to a non-default branch would require the bridge to authenticate as a different Subiekt user per branch — a session-architecture change, not a per-invoice API parameter — and is out of scope. This endpoint exists so an operator/OL admin can see which branches are configured.

### `GET /api/cash-registers` — cash-register stations (Stanowiska Kasowe) (issue #5)
Lists every Stanowisko Kasowe. `id` is the value to pass as `stanowiskoKasoweId` on `POST /api/invoices` (the only functional selector this issue ships). `oddzialId` on each row (and the optional `?oddzialId=` filter) is informational only, per the note above — it does not gate which stations are usable.

```json
{ "success": true, "data": { "count": 4, "cashRegisters": [
  { "id": 100065, "name": "Kasa Centralna", "symbol": "CENTR", "oddzialId": 100000 },
  { "id": 100066, "name": "Kasa Outlet", "symbol": "OUTLET", "oddzialId": null }
] } }
```

### Cash-register selection on `POST /api/invoices` (issue #5)
One ADDITIVE, optional field selects the cash-register station explicitly; absent keeps today's implicit-default behavior (no regression):

```json
{ "stanowiskoKasoweId": 100066 }
```

- Absent → the document's implicit-default station applies (today's behavior).
- Present → must be a positive id (400 `bad_request` otherwise); the document is issued using that station, with its branch left at the session's implicit default (see the `GET /api/branches` note above — branch cannot be selected).
- Not supported for `documentType: "PA"` (422).
- Live-verified end-to-end against a real Sfera connection (station-only selection saves successfully); see `docs/plans/implementation-plan-5-oddzial-stanowisko-selector.md` for the full investigation that led to this scope.

### `POST /api/customers/upsert` — address & dedup
- Dedup by `nip`: a repeat upsert of an existing NIP returns the existing `id` (keep-existing policy; no overwrite — logged).
- Optional structured `address` maps onto `AdresPodstawowy` (`ulica/nrDomu/nrLokalu/kodPocztowy/miejscowosc/poczta`).

### `GET /api/invoices/{id}/status` — KSeF 5-state
`regulatoryStatus`/`ksef.status` ∈ `none | pending | sent | accepted | rejected`; `clearanceReference` is the real `KSEF_ID` (Dokumenty.`NumerKSeF`), not the document number. Precise `rejected`/UPO detection is validated against a live KSeF.

---

## CORS Configuration

Bridge enables CORS for `http://localhost:5173` (cockpit).

**Allowed methods:** GET, POST, PUT, DELETE, OPTIONS
**Allowed headers:** Content-Type, Authorization

---

## Rate Limiting

No rate limiting in POC. Production deployment should add:
- Per-IP rate limits
- Exponential backoff for failed auth attempts
- Request timeouts for long-running operations
