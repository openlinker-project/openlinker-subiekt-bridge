# Implementation Plan: Bank account / payment method per invoice (Sfera adapter)

**Date**: 2026-07-02
**Status**: Ready for Review
**Issue**: openlinker-project/openlinker-subiekt-bridge#1
**Branch**: `1-bank-account-payment-method`
**Estimated Effort**: 2-3 days (including live verification on the Windows/Subiekt machine)

---

## 1. Task Summary

**Objective**: Let a bridge caller (OpenLinker) select the payment method and bank account for an issued invoice, list the seller's Subiekt bank accounts, and (stretch) flip the seller's default bank account - instead of today's unconditional delegation to Subiekt's defaults (`DodajPlatnosciDomyslne()` / `DodajDomyslnaPlatnoscNatychmiastowaNaKwoteDokumentu()` in `SferaDokumentySprzedazyService.Create`, step 6b).

**Context**: Mirrors the feature shipped on the OpenLinker side for inFakt (`openlinker#1303` / `#1308`): per-connection default payment method (cash/transfer) + a live bank-account picker for transfer invoices + two-way default-account sync. The Sfera reflection dump (`bridge/tools/SferaInspect/modeldanych-dump.txt`) confirms the object model supports it (`RachunekBankowy`, `FormaPlatnosci.RachunekBankowy`, `Dokument.RachunkiBankowe` of type `RachunekBankowyDokumentu`, writable `PodstawowyDlaWaluty` / `PodstawowyRachunekBankowy` flags), but nothing has been exercised against a live Subiekt yet.

**Classification**: Integration (Sfera adapter) + Infrastructure (SQL reader) + Application (ports) + Interface (API contract + endpoints) + Domain (value object)

### Decisions locked in (from the grill-me session)

| # | Decision |
|---|---|
| D1 | Work happens in this repo (fresh clone of `openlinker-project/openlinker-subiekt-bridge`), one PR with `Closes #1` carrying plan + implementation. |
| D2 | Bank-account **listing** is a plain SQL read (Dapper, `Subiekt.Bridge.Infrastructure.Sql`), matching the established 3A read pattern (`SqlStockReader`, `SqlDocumentStatusReader`). It does not touch the Sfera write queue. |
| D3 | Invoice-create contract gains `paymentMethod: "cash" \| "transfer"` + `bankAccountId` - mirroring the OL/inFakt neutral shape, not a bare `bankAccountId`. |
| D4 | **Strict validation**: `bankAccountId` is allowed ONLY with explicit `paymentMethod: "transfer"`; `transfer` REQUIRES `bankAccountId`; `cash` must NOT carry `bankAccountId`; any other combination is 422. Both fields absent = today's default behavior (zero regression). |
| D5 | The selection is modeled as a Domain value object `PaymentSelection` carried by `SalesDocument` (combination rules live in `PaymentSelection.TryCreate`, consistent with `Nip` / `VatRate` / `Money`). |
| D6 | cash/transfer maps to a `FormaPlatnosci` row by **configurable name** in `SferaOptions` with defaults `gotowka` -> `"gotówka"`, `transfer` -> `"przelew"` (case-insensitive, `Aktywna = 1`). |
| D7 | Stretch goal IS in scope: `PUT /api/bank-accounts/{id}/default` flips `PodstawowyDlaWaluty` through the Sfera write queue. It is DROPPED from the PR if the live probe shows the flag write is unsafe/unsupported (issue explicitly allows this). |
| D8 | **Probe first, code second**: a throwaway live verification against the real Subiekt database/Sfera runs BEFORE the full implementation, confirming schema + wiring mechanism. |

---

## 2. Scope & Non-Goals

### In Scope
- `GET /api/bank-accounts` - list the seller's bank accounts (id, number, bank number, description, currency, active, default-for-currency flag).
- `paymentMethod` + `bankAccountId` on `POST /api/invoices` (additive, optional pair).
- Issuance wiring in `SferaDokumentySprzedazyService`: explicit payment replaces the step-6b default calls when a selection is present; unchanged path otherwise.
- Stretch: `PUT /api/bank-accounts/{id}/default` (droppable per D7).
- Unit tests (Domain, Application, Api validators) + live-verification checklist.
- Docs: `docs/API_ENDPOINTS.md` update + findings written back to issue #1.

### Out of Scope
- OpenLinker-side changes (the OL Subiekt plugin consuming these endpoints is a separate issue in `openlinker-project/openlinker`).
- A per-order payment-method concept in OL core.
- Corrections (KOR): the correction path keeps inheriting payments from Sfera's correction BO; per-correction payment selection is not part of issue #1.
- Payment due date (`TerminPlatnosci`) control - stays whatever the resolved `FormaPlatnosci` row defines.
- Paragon (PA) payment selection: PA keeps today's immediate-payment default path. The validator rejects `paymentMethod`/`bankAccountId` on `documentType: "PA"` (422) to avoid silently ignoring caller intent.

### Constraints
- **No regression**: requests without the new fields must behave byte-for-byte like today (AC in issue #1).
- All Sfera writes go through `SferaWriteQueue` (single-writer serialization); reads use the separate SQL connection (3A).
- Reflection-dump evidence is static metadata; every Sfera-side behavior in this plan carries a live-verification step (issue #1 acceptance criteria).
- Development happens on WSL; live verification requires the operator's Windows machine with Subiekt nexo PRO + Sfera.

---

## 3. Architecture Mapping

**Target layers** (bridge hexagon, C#):

| Layer | Project | What changes |
|---|---|---|
| Domain | `Subiekt.Bridge.Domain` | New `PaymentSelection` VO + optional field on `SalesDocument` |
| Application | `Subiekt.Bridge.Application` | New ports: `IBankAccountsReader` (read), `IDefaultBankAccountWriter` (stretch) |
| Infrastructure.Sql | `Subiekt.Bridge.Infrastructure.Sql` | New `SqlBankAccountsReader` (Dapper) |
| Infrastructure.Sfera | `Subiekt.Bridge.Infrastructure.Sfera` | `SferaInvoiceInput` + `SferaDokumentySprzedazyService` step 6b branch; `SferaOptions` payment-form names; stretch: `SferaBankAccountService` + `SferaDefaultBankAccountWriter` adapter |
| Api | `Subiekt.Bridge.Api` | Contract fields + validators on `CreateInvoiceRequestDto`; new `BankAccountsEndpoints`; DI registration in `Program.cs` |

**Existing components reused**:
- `SqlConnectionFactory` / `SqlReadOptions` / `SqlErrorClassifier` / `LimitGuard` (read path).
- `SferaWriteQueue` + `SferaObjectAccessor` + `SferaReflection` (write path).
- `ResponseEnvelope` / `BridgeError` / `EndpointHelpers.BridgeFail` / `IAuditLog` (API surface).
- `Result` / `Error` domain primitives; `InvoiceContractMapper.Build` as the single DTO -> Domain seam.
- `IssueInvoiceHandler` is untouched: `PaymentSelection` rides inside `SalesDocument`, so the idempotency policy and the build/issue stage split keep working unchanged.

**Boundary justification**: the payment-selection RULE (which combinations are legal) is business logic -> Domain. The MAPPING of cash/transfer to a concrete `FormaPlatnosci` row and the document wiring are Sfera specifics -> Infrastructure.Sfera. Listing accounts is a read-model -> Infrastructure.Sql behind an Application port. No OL-core vocabulary leaks into the bridge; the wire contract mirrors the OL-neutral `paymentMethod`/`bankAccountId` shape.

---

## 4. External / Domain Research

### Sfera object model (from `modeldanych-dump.txt`, static - Phase 0 verifies live)

- `InsERT.Moria.ModelDanych.RachunekBankowy` (line ~76315): `Id`, `Numer`, `NumerBanku`, `NumerBezSeparatorow`, `Opis`, `Waluta` (nav), `Aktywny`, `PodstawowyDlaWaluty`, `Wlasciciel` (a `Podmiot`), `MojaFirmaId`, `FormyPlatnosci` (collection), `DokumentyNaKtorychWybrany` (collection).
- `InsERT.Moria.ModelDanych.FormaPlatnosci` (line ~38001): `Id`, `Nazwa`, `Aktywna`, `RachunekBankowy` (nav), `PodstawowyRachunekBankowy`, `PodstawowyRachunekDlaWaluty`, `TerminPlatnosci`, `DokumentyNaKtorychUzyta`.
- `Dokument` family: `FormaPlatnosci` / `FormaPlatnosciId` (nullable int, line ~2797-2798), `FormyPlatnosci` (collection of `FormaPlatnosciDokumentu`), `RachunekBankowyKlienta` (+Id), `RachunkiBankowe` of type `RachunekBankowyDokumentu` (line ~2920) - the issue's `RachunekBankowyDokumentu.RachunekBankowyMojejFirmy` path.
- Expected SQL tables (naming convention observed for other entities): `ModelDanychContainer.RachunkiBankowe`, `ModelDanychContainer.FormyPlatnosci`. Exact table/column names are **unconfirmed** until Phase 0.

### Wiring mechanism for the per-invoice override - VERIFIED LIVE (2026-07-02)

Phase 0 ran on the live demo (FS 176/178/179 in `Nexo_Demo_1`); full evidence in `docs/spikes/bank-account-probe-findings.md`. The confirmed sequence for an explicit selection:

1. Resolve the `FormaPlatnosci` entity INSIDE the document's unit of work via FK fixup (`Dane.FormaPlatnosciId = id` then read `Dane.FormaPlatnosci`). Facade-resolved entities live in a different ObjectContext and must not be assigned across contexts.
2. Add the payment row through the `IPlatnosciNaDokumencie` interface (explicitly implemented on the BO - reflection lookup must go through the interface type): transfer -> `DodajPlatnoscOdroczona(FormaPlatnosci)`, cash -> `DodajPlatnoscNatychmiastowa(FormaPlatnosci)`.
3. Set `Dane.FormaPlatnosci` so the document header carries the chosen form.
4. Transfer only: write the seller-account snapshot `Dane.RachunkiBankowe.RachunekBankowyMojejFirmy` (`DaneRachunkuBankowego { Nazwa, Numer }`; the 1:1 `RachunekBankowyDokumentu` row is pre-attached on a fresh BO).
5. Do NOT call `DodajPlatnosciDomyslne()` on the explicit path - it derives from configuration and ignores `Dane.FormaPlatnosci`.

Dead end (verified): `AktualizujRachunkiBankowe(string[], out string)` updates the BUYER's accounts, not the seller's.

### Internal patterns followed
- Read endpoints: `StockEndpoints` -> `IStockReader` -> `SqlStockReader` (no use-case layer for pure reads; endpoint calls the port directly).
- Write use case: `UpsertCustomerHandler` / `IssueInvoiceHandler` (pure orchestration in Application; adapter enqueues into `SferaWriteQueue`).
- Validation: `Validators.cs` + `ValidationGate.TryValidate` returning a 422 envelope.
- Error taxonomy: `BridgeException.Classify` -> `unreachable` / `rejected` / `bad_request` envelope codes.

---

## 5. Questions & Assumptions

### Open questions - ALL ANSWERED by the Phase 0 live probe (2026-07-02, see `docs/spikes/bank-account-probe-findings.md`)
- **Q1** ANSWERED: accounts live in `CentraGromadzeniaFinansow_RachunekBankowy` (TPT subtype; name on the base table), currency via `Waluta_Id -> Waluty.Symbol`, seller scoping via `Wlasciciel_Id = <MojaFirma Podmiot>` (`Typ=2, Podtyp=11`). The UI "Podstawowy" flag is `WlascicielPodstawowego_Id IS NOT NULL`, NOT `PodstawowyDlaWaluty`.
- **Q2** ANSWERED: FormaPlatnosci on the header + explicit payment row + seller-account snapshot (`RachunekBankowyMojejFirmy {Nazwa, Numer}` on the 1:1 `RachunekBankowyDokumentu`). Verified end-to-end on FS 178.
- **Q3** ANSWERED: `IPlatnosciNaDokumencie.DodajPlatnoscOdroczona(FormaPlatnosci)` / `DodajPlatnoscNatychmiastowa(FormaPlatnosci)` (interface-typed lookup); FormaPlatnosci resolved via FK fixup in the document's UoW. `DodajPlatnosciDomyslne` ignores `Dane.FormaPlatnosci` and must not be used on the explicit path.
- **Q4** ANSWERED - stretch STAYS IN SCOPE: the default flip works via the Podmiot BO (`IPodmioty.Znajdz` -> `Dane.RachunekPodstawowy = <account from Dane.Rachunki>` -> `Zapisz()`); previous default clears automatically. Direct write of `RachunekBankowy.WlascicielPodstawowego` is unsponsored (throws) - dead end, avoided.
- **Q5** ANSWERED: stock install ships `Gotówka` (Id 1) and `Przelew` (Id 2, type "Płatność odroczona", 7-day term). D6 name-based config defaults hold.

### Assumptions (safe defaults)
- **A1**: `bankAccountId` on the wire is the Subiekt `RachunkiBankowe.Id` (int) - the same id the list endpoint returns. No separate identifier mapping (bridge is single-tenant, ids are provider-native, consistent with `KontrahentId`).
- **A2**: An idempotent replay of `POST /api/invoices` returns the previously issued document regardless of payment fields in the retried body (idempotency key wins) - consistent with existing semantics.
- **A3**: Currency guard: if the selected account's currency differs from the document currency, the request is rejected (422) at the SQL pre-check stage. Cheap to enforce, prevents a nonsensical PLN invoice pointing at an EUR account.
- **A4**: A `bankAccountId` that does not exist or is inactive -> `rejected` (HTTP 422 envelope with `validation`/`rejected` code), checked in `SferaDokumentySprzedazyService` via SQL before the BO is built (mirrors the existing kontrahent existence check).
- **A5**: For `cash`, the resolved cash `FormaPlatnosci` participates in the immediate-payment call so the paid-amount behavior matches today's `DodajDomyslnaPlatnoscNatychmiastowaNaKwoteDokumentu` semantics; for `transfer`, no immediate payment is added (invoice becomes a receivable with the form's `TerminPlatnosci`). Verified in Phase 0.

### Documentation gaps
- `docs/API_ENDPOINTS.md` does not cover bank accounts (noted in issue #1) - updated in Phase 6.

---

## 6. Proposed Implementation Plan

### Phase 0 - Live probe (Windows machine, throwaway) - DONE 2026-07-02
**Status**: COMPLETED. Tool: `bridge/tools/BankAccountProbe`; findings: `docs/spikes/bank-account-probe-findings.md` (+ posted to issue #1). Evidence documents FS 176/178/179 in the demo DB. All Phase 0 steps below were executed; the sub-steps are kept for the record.
**Goal**: convert every "static dump says" into "live Subiekt confirms" before writing production code. Deliverable: findings appended to issue #1.

1. **SQL schema probe**
   - **Where**: run against the live Subiekt SQL Server (SSMS/sqlcmd), queries prepared in `docs/spikes/bank-accounts-probe.sql` (new file, committed for reproducibility).
   - **Action**: `SELECT TOP 5 *` + `INFORMATION_SCHEMA.COLUMNS` for `ModelDanychContainer.RachunkiBankowe` and `ModelDanychContainer.FormyPlatnosci`; identify the seller-scoping column (Q1), the currency join, and the default flags; list `FormyPlatnosci` names (Q5).
   - **Acceptance**: exact SELECT for the list endpoint is written down and returns the accounts visible in Subiekt's "Rachunki bankowe" config screen, correctly scoped.
2. **Sfera wiring probe**
   - **Where**: extend `bridge/tools/SferaInspect` (or a scratch console) reusing `SferaBoot`/`SferaSession`.
   - **Action**: create a throwaway FV against the demo DB four ways: (a) baseline defaults; (b) route 1 transfer + explicit account; (c) route 2 account only; (d) cash via resolved cash form. Read back `Dane.FormaPlatnosciId`, `FormyPlatnosci` rows and `RachunkiBankowe.RachunekBankowyMojejFirmy`; check the printed document/UI shows the account (Q2/Q3/A5).
   - **Acceptance**: one mechanism demonstrably lands the selected account on the saved document; findings note which.
3. **Default-flag write probe (stretch gate)**
   - **Action**: load a `RachunekBankowy` BO, set `PodstawowyDlaWaluty`, save, verify in UI + SQL; restore original state (Q4).
   - **Acceptance**: go/no-go decision for D7 recorded in issue #1.

### Phase 1 - Domain
1. **`PaymentSelection` value object**
   - **File**: `bridge/Subiekt.Bridge.Domain/Invoices/PaymentSelection.cs`
   - **Action**: immutable VO: `PaymentMethod Method` (`Cash | Transfer`, `as const`-style C# enum or static values per repo convention) + `int? BankAccountId`. `TryCreate(string? method, int? bankAccountId)` returns `Result<PaymentSelection?>` implementing D4 strict rules: both absent -> success(null); `transfer` requires positive `bankAccountId`; `cash` forbids it; `bankAccountId` without method -> failure; unknown method string -> failure. Error codes `payment.method`, `payment.bankAccount`.
   - **Acceptance**: `PaymentSelectionTests` covers the full combination matrix (8+ cases).
2. **`SalesDocument` carries the selection**
   - **File**: `bridge/Subiekt.Bridge.Domain/Invoices/SalesDocument.cs`
   - **Action**: optional `PaymentSelection? Payment` constructor/Create parameter (default null). `Create` additionally rejects a `PaymentSelection` on `DocumentType.PA` (`doc.payment.pa` error). `FoldDiscounts()` preserves the field.
   - **Acceptance**: `SalesDocumentTests` extended: PA+payment rejected, FV+payment preserved through folding, absent payment stays null.

### Phase 2 - Application ports
1. **`IBankAccountsReader`**
   - **File**: `bridge/Subiekt.Bridge.Application/Ports/IBankAccountsReader.cs`
   - **Action**: `Task<Result<IReadOnlyList<BankAccountView>>> ListAsync(CancellationToken)` + `BankAccountView(int Id, string Numer, string? NumerBanku, string? Opis, string Waluta, bool Aktywny, bool PodstawowyDlaWaluty)` (record colocated in the port file, matching `IStockReader`'s `WarehouseView` style).
   - **Acceptance**: compiles; architecture tests (`DependencyRuleTests`) stay green.
2. **`IDefaultBankAccountWriter`** (stretch, D7)
   - **File**: `bridge/Subiekt.Bridge.Application/Ports/IDefaultBankAccountWriter.cs`
   - **Action**: `Task<Result<Unit>> SetDefaultAsync(int bankAccountId, CancellationToken)` - semantics: set `PodstawowyDlaWaluty = true` on the target and clear it on other accounts of the same currency.
   - **Acceptance**: compiles; dropped cleanly if Phase 0 gate fails.

### Phase 3 - Infrastructure.Sql (read path)
1. **`SqlBankAccountsReader`**
   - **File**: `bridge/Subiekt.Bridge.Infrastructure.Sql/SqlBankAccountsReader.cs`
   - **Action**: Dapper query (verified SQL in `docs/spikes/bank-account-probe-findings.md` s.1) over `CentraGromadzeniaFinansow_RachunekBankowy` JOIN `CentraGromadzeniaFinansow` (name) LEFT JOIN `Waluty`, scoped to `Wlasciciel_Id = <MojaFirma Podmiot>` and `Aktywny = 1`, with `isDefault = (WlascicielPodstawowego_Id IS NOT NULL)`, ordered default-first. Errors classified via `SqlErrorClassifier` (mirrors `SqlStockReader`).
   - **Acceptance**: returns the same rows as the Phase 0 probe on the live machine.

### Phase 4 - Infrastructure.Sfera (write path)
1. **`SferaOptions` payment-form names (D6)**
   - **File**: `bridge/Subiekt.Bridge.Infrastructure.Sfera/SferaOptions.cs` (+ `appsettings.json` sample in Api)
   - **Action**: `CashPaymentFormName = "gotówka"`, `TransferPaymentFormName = "przelew"`.
2. **`SferaInvoiceInput` extension**
   - **File**: `bridge/Subiekt.Bridge.Infrastructure.Sfera/SferaInputs.cs`
   - **Action**: add `SferaPaymentInput? Payment` (`record SferaPaymentInput(string Method, int? BankAccountId)`) to `SferaInvoiceInput`.
3. **`SferaInvoiceIssuer.ToInput` mapping**
   - **File**: `bridge/Subiekt.Bridge.Infrastructure.Sfera/Adapters/SferaInvoiceIssuer.cs`
   - **Action**: translate `document.Payment` into `SferaPaymentInput`.
4. **Issuance wiring (core change)**
   - **File**: `bridge/Subiekt.Bridge.Infrastructure.Sfera/SferaDokumentySprzedazyService.cs` (step 6b, currently lines 151-153)
   - **Action**:
     - No `Payment` -> today's two `InvokeIfExists` calls, verbatim (regression guard).
     - `Payment` present -> SQL pre-checks: bank account exists + active + currency matches document (A3/A4); resolve the configured `FormaPlatnosci` row by name (D6) and fail `rejected` when missing/inactive; then apply the Phase-0-CONFIRMED mechanism (findings s.3): FK-fixup the `FormaPlatnosci` entity in the document's UoW, add the payment via `IPlatnosciNaDokumencie.DodajPlatnoscOdroczona(fp)` (transfer) / `DodajPlatnoscNatychmiastowa(fp)` (cash), set `Dane.FormaPlatnosci`, and for transfer write the seller snapshot `Dane.RachunkiBankowe.RachunekBankowyMojejFirmy {Nazwa, Numer}`. No `DodajPlatnosciDomyslne` on this path.
     - Keep step 6c..8 unchanged; extend the pre-save debug dump with `FormaPlatnosciId` (already listed) + the document bank-account id.
   - **Acceptance**: live: FV issued with `transfer`+account shows that account on the document (issue AC); FV with `cash` books an immediate payment; FV without fields identical to pre-change behavior.
5. **Stretch: default-account writer (D7)**
   - **Files**: `bridge/Subiekt.Bridge.Infrastructure.Sfera/SferaRachunkiBankoweService.cs` (BO logic) + `bridge/Subiekt.Bridge.Infrastructure.Sfera/Adapters/SferaDefaultBankAccountWriter.cs` (port adapter, enqueues via `SferaWriteQueue`)
   - **Action**: Phase-0-CONFIRMED path (findings s.6): load the seller Podmiot BO via `IPodmioty.Znajdz(x => x.Id == ownerId)`, locate the target account in `Dane.Rachunki`, set `Dane.RachunekPodstawowy`, `Zapisz()` - the previous default clears automatically. (NOT `PodstawowyDlaWaluty`, and NOT a direct `WlascicielPodstawowego` write - unsponsored, throws.)
   - **Acceptance**: after the call, Subiekt's config screen shows the new default; second call is idempotent.

### Phase 5 - Api (contract + endpoints)
1. **Contract fields**
   - **File**: `bridge/Subiekt.Bridge.Api/Models/ResponseEnvelope.cs` (`CreateInvoiceRequestDto`)
   - **Action**: add `public string? PaymentMethod { get; set; }` (`"cash" | "transfer"`) and `public int? BankAccountId { get; set; }` with doc comments (additive; optional).
2. **Validator (D4)**
   - **File**: `bridge/Subiekt.Bridge.Api/Validation/Validators.cs` (`InvoiceValidators.Create`)
   - **Action**: strict-combination rules + PA rejection; all failures 422 with field-level messages.
3. **Mapper**
   - **File**: `bridge/Subiekt.Bridge.Api/Contracts/InvoiceContracts.cs`
   - **Action**: `InvoiceContractMapper.Build` calls `PaymentSelection.TryCreate` and threads the result into `SalesDocument.Create` (failures surface as the existing 422 build-failure path via `IssueInvoiceHandler.BuildFailedCode`).
4. **`BankAccountsEndpoints`**
   - **File**: `bridge/Subiekt.Bridge.Api/Endpoints/BankAccountsEndpoints.cs` (new) + `Program.cs` registration
   - **Action**: `GET /api/bank-accounts` -> `IBankAccountsReader.ListAsync`, `ResponseEnvelope<{ accounts: [...] }>`, audit-logged (`ListBankAccounts`), standard Bearer auth. Stretch: `PUT /api/bank-accounts/{id}/default` -> `IDefaultBankAccountWriter`, audit-logged (`SetDefaultBankAccount`), 422 for unknown id.
5. **DI registration**
   - **File**: `bridge/Subiekt.Bridge.Api/Program.cs`
   - **Action**: register `SqlBankAccountsReader` as `IBankAccountsReader` (and stretch writer), mirroring `SqlStockReader` registration.

### Phase 6 - Tests, docs, verification
1. **Unit tests**
   - `bridge/tests/Subiekt.Bridge.Domain.Tests/PaymentSelectionTests.cs` - combination matrix.
   - `bridge/tests/Subiekt.Bridge.Domain.Tests/SalesDocumentTests.cs` - PA guard + fold preservation.
   - `bridge/tests/Subiekt.Bridge.Api.Tests/` - validator matrix for the new fields (follows existing validator-test style if present; otherwise endpoint-level validator tests).
   - `SferaInvoiceIssuer.ToInput` mapping cases (extend existing adapter/mapper tests if present in `Application.Tests`).
2. **Docs**
   - `docs/API_ENDPOINTS.md`: document `GET /api/bank-accounts`, the new invoice fields, strict-validation semantics, and (stretch) the default endpoint.
3. **Live verification checklist (issue AC)**
   - Scripted curl sequence (list accounts -> issue FV transfer+account -> read status -> inspect document in Subiekt UI/SQL -> issue FV without fields -> confirm unchanged defaults -> cash FV -> stretch default flip).
   - Findings written back to issue #1 before the PR leaves draft (explicit AC in the issue).

---

## 7. Alternatives Considered

1. **Sfera-reflection read for the account list (as literally proposed in the issue)** - rejected: every read would serialize through the write queue and require a connected Sfera session; the repo's established 3A pattern (separate SQL connection) is simpler, faster and already proven for stock/status/customers. The reflection path survives only as the Phase 0 probe.
2. **Bare `bankAccountId` without `paymentMethod`** - rejected in grill-me (D3): the OL-side feature is method+account; mirroring the shape now avoids a second contract change when OL wires the Subiekt plugin.
3. **Exposing `formaPlatnosciId` natively** - rejected: requires a second discovery endpoint, couples OL to Subiekt-internal ids, and does not map onto the OL-neutral cash/transfer model.
4. **Liberal semantics (inferring transfer from `bankAccountId`)** - rejected by the user in grill-me: strict validation (D4) keeps the contract unambiguous and the live-verification surface minimal.
5. **Raw SQL UPDATE for the default flag (stretch)** - rejected: bridge policy is BO-mediated writes only (CRC/TimeStamp columns and Sfera invariants make raw writes unsafe); if the BO write fails the probe, the stretch is dropped rather than degraded to SQL.

---

## 8. Validation & Risks

### Architecture compliance
- Hexagon respected: Domain rule in Domain, ports in Application, Dapper + reflection in Infrastructure, contract + validation in Api. `DependencyRuleTests` guard it.
- All writes stay behind `SferaWriteQueue`; all reads on the 3A SQL connection.

### Risks
- **R1 - dump vs live drift**: table/column names or BO behavior differ from the reflection dump. Mitigated by Phase 0 running before implementation; SQL text lives in one constant per reader.
- **R2 - wiring mechanism does not stick** (neither route lands the account on the saved document). Mitigated: Phase 0 tests both routes; worst case the per-invoice override AC is renegotiated on the issue with evidence attached.
- **R3 - `FormaPlatnosci` name mismatch on non-standard installs**: mitigated by D6 configurability + a clear `rejected` error naming the missing form.
- **R4 - regression on the default path**: mitigated by keeping the no-selection branch byte-identical and by the live no-fields verification step.
- **R5 - stretch flag write has side effects** (e.g. Subiekt recalculates defaults elsewhere): mitigated by probe 3 + restore, and by D7's drop clause.
- **R6 - GPG/DCO**: repo requires signed commits; all commits `git commit -s` with GPG per global config.

### Edge cases
- `bankAccountId <= 0` -> 422 (validator).
- Inactive account / wrong currency / unknown id -> `rejected` 422 with a message naming the id (A3/A4).
- Idempotent replay with different payment fields -> prior document returned (A2, documented in API doc).
- PA + payment fields -> 422 (Scope note + Domain guard, double-enforced).
- Empty account list (fresh install) -> `Success` with empty array (not an error).

### Backward compatibility
- Additive contract only; absent fields = legacy behavior. No DB migrations (bridge owns no schema). Cockpit unaffected.

---

## 9. Testing Strategy & Acceptance Criteria

### Unit tests (runnable on WSL, no Sfera needed)
- `PaymentSelectionTests` - full D4 matrix.
- `SalesDocumentTests` - PA guard, fold preservation, null default.
- Validator tests - wire-level combination matrix incl. PA.
- `SferaInvoiceIssuer.ToInput` - payment mapping present/absent.

### Live verification (Windows, replaces integration tests - no Testcontainers story for Sfera)
- Phase 0 probe findings + Phase 6 checklist executed against the demo Subiekt DB; results pasted into issue #1.

### Acceptance criteria (mirrors issue #1)
- [ ] `GET /api/bank-accounts` returns the seller's accounts (number, bank, currency, default flag), verified live.
- [ ] `POST /api/invoices` accepts optional `paymentMethod` + `bankAccountId` under strict D4 validation.
- [ ] Transfer invoice with an account verifiably carries that account on the Subiekt document (`RachunekBankowyDokumentu` / UI).
- [ ] Cash invoice books the immediate payment via the configured cash form.
- [ ] Requests without the new fields behave exactly as before (default calls still fire).
- [ ] Stretch: `PUT /api/bank-accounts/{id}/default` flips the default flag (or is dropped with probe evidence).
- [ ] Unit tests added in `bridge/tests/` for the VO, document guard, validator and mapper.
- [ ] Live findings written back to issue #1 before merge.

---

## 10. Alignment Checklist

- [x] Follows hexagonal architecture (bridge projects' layering + `DependencyRuleTests`)
- [x] Respects read (SQL 3A) vs write (Sfera queue) separation
- [x] Uses existing patterns (`SqlStockReader`, `Validators`, `ResponseEnvelope`, `Result`)
- [x] Idempotency considered (A2 - existing `IssueInvoiceHandler` gate untouched)
- [x] Error handling comprehensive (validator 422, `rejected`/`unreachable` taxonomy, A3/A4 pre-checks)
- [x] Testing strategy complete (unit matrix + live checklist standing in for integration tests)
- [x] No-regression fallback explicit (step 6b default branch preserved verbatim)
- [x] Plan is execution-ready (Phase 0 gate resolves every open question before production code)
