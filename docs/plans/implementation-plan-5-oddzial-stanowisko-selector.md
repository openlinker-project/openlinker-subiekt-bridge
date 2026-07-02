# Implementation Plan: Oddział/Stanowisko Kasowe selector + discovery endpoints

**Date**: 2026-07-02 (rescoped 2026-07-03)
**Status**: Implemented, self-reviewed, live-verified — **RESCOPED to Stanowisko-only**
**Issue**: openlinker-project/openlinker-subiekt-bridge#5
**Branch**: `5-oddzial-stanowisko-selector`
**Estimated Effort**: 1 day (including live verification via WSL→Windows, no separate handoff session needed)

---

## 0. SCOPE REVISION (2026-07-03) — branch (Oddzial) selection removed, not achievable

A second independent tech-review pass caught that this plan's §6 "live verification" never actually proved a non-default branch works — the one ACCEPTED run that set an Oddzial used `100000`, which is the document's own implicit-default (head-office) unit, not a real branch. Two further live experiments (documented in `docs/spikes/podmioty-oddzial-stanowisko-probe-findings.md` §8) settled it definitively:

1. Creating the document via `ParametryTworzeniaDokumentu` (Magazyn+Oddzial+Stanowisko bundled at creation time, discovered in the reflection dump as a plausible "correct" mechanism) with a real branch (`100001`) — still rejected, identically to the post-creation patch this plan originally used.
2. `IKontekstBiznesowy` — the session's business context (Oddzial/Magazyn/StanowiskoKasowe/Podmiot/RachunekBankowy), resolved live, is **entirely read-only** and matches exactly the implicit-default values observed everywhere in this investigation. A document's operative branch comes from the **logged-in session**, not any per-document field.

**Conclusion**: routing an invoice to a non-default branch would require the bridge to authenticate as a different Subiekt user per branch (a session-architecture change), not a per-invoice API parameter. This is out of scope for issue #5 as originally framed.

**Rescoped**: `oddzialId` removed entirely from the write contract. `stanowiskoKasoweId` (live-verified working with the implicit-default branch) ships as the only functional selector. `GET /api/branches` stays as an informational-only listing. `BranchSelection` (Domain) renamed to `CashRegisterSelection` and simplified to one field. The rest of this document (sections 1-6) describes the ORIGINAL, broader scope and is kept for historical record of the investigation — read section 0 as the authoritative current state.

---

## 1. Task Summary

**Objective**: Let a caller route a specific invoice to a specific branch (Oddzial) and cash-register station (Stanowisko Kasowe), and discover valid options - closing the gap issue #3/PR #4 explicitly deferred (bank-account ownership was fixed there; branch/station routing was not).

**Context**: Live probing (`docs/spikes/podmioty-oddzial-stanowisko-probe-findings.md` §1-7, same day) resolved every Phase 1 unknown needed to design this safely:
- Oddzial and Podmiot/Płatnik are independent axes (confirmed on demo data: 1 Podmiot, 2 Oddziały).
- The document's branch field is `Dane.MiejsceWprowadzenia` (not literally "Oddzial").
- Setting a non-default branch alone (no explicit station) breaks Sfera's implicit cash-register resolution.
- A station linked to a *different* Oddzial than the document's triggers `StanowiskoKasoweZInnejJednostkiOrganizacyjnejBlad` - confirmed live via the operator's own UI trace, which showed it is a **soft, confirmable warning** ("ZAPISZ MIMO TO"), not a hard error.
- **Operator decision**: the bridge must reproduce that confirm-then-proceed UX - never silently auto-accept an unconfirmed mismatch.

**Classification**: Domain (new `BranchSelection` value object) + Application (2 new read ports) + Infrastructure.Sql (2 new readers) + Infrastructure.Sfera (write-path wiring + pre-check) + Interface (2 new endpoints + DTO fields).

---

## 2. Design Decision: Front-Loaded Validation Instead of a Confirm/Retry Flow

The operator's "reproduce the confirm-then-proceed UX" requirement could be read two ways:
- **(a)** Let the bridge attempt the save, catch Sfera's warning, and require a second confirmed call to proceed (literally mirroring "ZAPISZ MIMO TO").
- **(b)** Since the bridge can predict the exact condition that triggers the warning (a `StanowiskoKasoweJednostkaOrganizacyjna` link-table mismatch) via its own SQL, validate it BEFORE ever calling Sfera and reject with a specific, actionable message if invalid - the caller then resubmits with a *correct* pair (discoverable via `GET /api/cash-registers`), never a "confirmed anyway" flag.

**Chosen: (b).** Reasons:
- It never silently overrides the warning (satisfies the hard requirement) while avoiding a second wire-protocol concept (a `confirmWarnings` flag with its own semantics, expiry, idempotency implications).
- It was never confirmed live that Sfera exposes a programmatic "confirm and proceed" hook reachable outside the desktop client's dialog flow - reflection archaeology found no such API distinct from the ones already tried. Building a confirm/retry flow around an unconfirmed mechanism would be speculative; front-loading the check needs only the schema already read for discovery.
- It is provably correct against every live data point gathered: 3 end-to-end runs through the real `SferaDokumentySprzedazyService` (mismatched pair → rejected pre-Sfera; valid pair → accepted; station-only → accepted) all matched the predicted outcome exactly (see §6).

---

## 3. Scope & Non-Goals

### In Scope
- Domain: `BranchSelection` value object (mirrors `PaymentSelection`) - combination rules: both absent = no selection; `oddzialId` alone = rejected; `stanowiskoKasoweId` alone = allowed; both = allowed (cross-check deferred to issuance). `SalesDocument` carries `Branch` and rejects it on `documentType: "PA"` (mirrors the existing payment guard).
- Application: `IBranchesReader` / `ICashRegistersReader` ports (mirror `IBankAccountsReader`).
- Infrastructure.Sql: `SqlBranchesReader` / `SqlCashRegistersReader` (mirror `SqlBankAccountsReader`'s 3A read pattern).
- Infrastructure.Sfera: `SferaDokumentySprzedazyService.ApplyExplicitBranch` - sets `MiejsceWprowadzeniaId`/`StanowiskoKasoweId` via the FK-set + EF-fixup pattern already proven for `FormaPlatnosciId`; when `oddzialId` is present, pre-checks the station's link-table row matches it EXACTLY before touching the BO at all.
- Interface: `GET /api/branches`, `GET /api/cash-registers` (optional `?oddzialId=` filter); `CreateInvoiceRequestDto` gains `oddzialId`/`stanowiskoKasoweId`; `InvoiceValidators` gain shape rules (positivity only - combination rules are Domain).
- Tests: `BranchSelectionTests`, `SalesDocumentTests` branch cases, `InvoiceBranchContractTests` (validator + mapper) - all mirror the issue #1 payment-selection test suites.
- `docs/API_ENDPOINTS.md` updated.
- Live verification: 3 end-to-end runs via a new `branch-issuance-test` probe subcommand, exercising the REAL `SferaDokumentySprzedazyService` (not a standalone reflection experiment) - see §6.

### Out of Scope
- A `confirmWarnings`-style second-call flow (see §2 - rejected in favor of front-loaded validation).
- Correcting the interim `LogWarning`-only cross-payer guard from issue #3/PR #4 - that guard is about bank-account OWNER (Podmiot), a different axis from Oddzial/Stanowisko; a hard owner-vs-payer check there still needs the still-unresolved multi-Podmiot production topology (issue #3's remaining open item).
- Verification against the operator's REAL production database - all live verification in this plan ran against the `Nexo_Demo_1` demo database (single Podmiot, but a genuine multi-Oddzial/multi-Stanowisko topology sufficient to exercise every code path this issue touches).
- The `PUT /api/bank-accounts/{id}/default` default-scope-per-payer work (issue #3, separate axis).

### Constraints
- **No regression**: requests without `oddzialId`/`stanowiskoKasoweId` behave byte-for-byte like today - verified (`Build_WithoutBranchFields_ProducesDocumentWithNullBranch`, plus the existing 114 tests all still pass unmodified).
- Reads stay on the 3A pattern; writes stay on `SferaWriteQueue` (unchanged - the branch selection rides inside the same `SalesDocument`/`SferaInvoiceInput` already queued for payment).

---

## 4. Architecture Mapping

| Layer | Project | What changes |
|---|---|---|
| Domain | `Subiekt.Bridge.Domain` | New `BranchSelection` VO; `SalesDocument` gains `Branch` + PA guard |
| Application | `Subiekt.Bridge.Application` | New ports: `IBranchesReader`, `ICashRegistersReader` |
| Infrastructure.Sql | `Subiekt.Bridge.Infrastructure.Sql` | New `SqlBranchesReader`, `SqlCashRegistersReader` |
| Infrastructure.Sfera | `Subiekt.Bridge.Infrastructure.Sfera` | `SferaBranchInput`; `SferaDokumentySprzedazyService.ApplyExplicitBranch`; `SferaInvoiceIssuer.ToInput` maps `Branch` |
| Api | `Subiekt.Bridge.Api` | `CreateInvoiceRequestDto` fields; `InvoiceContractMapper` wiring; `InvoiceValidators` shape rules; new `BranchesEndpoints`; DI registration in `Program.cs` |
| tools | `bridge/tools/BankAccountProbe` | `branch-issuance-test` subcommand (real end-to-end verification, not a probe-only experiment) |

**Boundary justification**: identical shape to issue #1's `PaymentSelection` - the combination RULE is Domain; the cross-referential DB validation (station-owner-matches-Oddzial, mirroring bank-account-owner-matches-seller) is Infrastructure.Sfera, checked at issuance time via the same SQL connection already open on the document's unit of work.

---

## 5. Step-by-Step Plan (as implemented)

1. `Subiekt.Bridge.Domain/Invoices/BranchSelection.cs` - new VO, `TryCreate(oddzialId, stanowiskoKasoweId)`.
2. `Subiekt.Bridge.Domain/Invoices/SalesDocument.cs` - `Branch` property, `Create(...)` param, PA guard, `FoldDiscounts()` carries it through.
3. `Subiekt.Bridge.Application/Ports/IBranchesReader.cs`, `ICashRegistersReader.cs` - new ports + view records.
4. `Subiekt.Bridge.Infrastructure.Sql/SqlBranchesReader.cs`, `SqlCashRegistersReader.cs` - Dapper readers; schema confirmed live (§6.3 below) before writing the SQL, avoiding the column-name/type guesses that bit issue #3/PR #4's first live run.
5. `Subiekt.Bridge.Infrastructure.Sfera/SferaInputs.cs` - `SferaBranchInput` record; `SferaInvoiceInput.Branch`.
6. `Subiekt.Bridge.Infrastructure.Sfera/SferaDokumentySprzedazyService.cs` - `ApplyExplicitBranch` (pre-check + FK-set), invoked in `Create(...)` right before the payment step (order matters - see docs/spikes §6).
7. `Subiekt.Bridge.Infrastructure.Sfera/Adapters/SferaInvoiceIssuer.cs` - `ToInput` maps `document.Branch` → `SferaBranchInput`.
8. `Subiekt.Bridge.Api/Models/ResponseEnvelope.cs` - `CreateInvoiceRequestDto.OddzialId`/`StanowiskoKasoweId`.
9. `Subiekt.Bridge.Api/Contracts/InvoiceContracts.cs` - `InvoiceContractMapper.Build` calls `BranchSelection.TryCreate` and threads it into `SalesDocument.Create`.
10. `Subiekt.Bridge.Api/Validation/Validators.cs` - shape rules for the two new fields.
11. `Subiekt.Bridge.Api/Endpoints/BranchesEndpoints.cs` - `GET /api/branches`, `GET /api/cash-registers`.
12. `Subiekt.Bridge.Api/Program.cs` - DI + endpoint mapping.
13. Tests: `BranchSelectionTests.cs`, `SalesDocumentTests.cs` additions, `InvoiceBranchContractTests.cs`.
14. `docs/API_ENDPOINTS.md` - new endpoints + selection semantics section.
15. `bridge/tools/BankAccountProbe/Program.cs` - `branch-issuance-test` subcommand for real end-to-end verification.

## 6. Validation

- **Build**: `dotnet build bridge.sln` - 0 errors (verified via WSL→`powershell.exe`, same machine as the demo Sfera install - no separate Windows handoff session needed for this issue).
- **Unit tests**: 135/135 passing (was 114 before this issue; +21 new: 8 `BranchSelectionTests`, 4 `SalesDocumentTests` branch cases, 9 `InvoiceBranchContractTests`).
- **Live end-to-end** (via `branch-issuance-test`, exercising the real `SferaDokumentySprzedazyService` against `Nexo_Demo_1`, not a standalone reflection probe):
  - `--oddzial 100001 --stanowisko 100065` (mismatched - 100065 is linked to Oddzial 100000) → **REJECTED pre-Sfera** with `"Stanowisko kasowe 100065 nie pochodzi z oddziału 100001 (jest przypisane do oddziału 100000)."` - no wasted Sfera round-trip, no ambiguous generic message.
  - `--oddzial 100000 --stanowisko 100065` (matching pair) → **ACCEPTED**, real FV saved (`FS 185/CENTRALA/2026`).
  - `--stanowisko 100066` (station-only, no Oddzial) → **ACCEPTED**, real FV saved (`FS 186/CENTRALA/2026`).
- **Discovery SQL verified live**: both `SqlBranchesReader` and `SqlCashRegistersReader` queries run directly against `Nexo_Demo_1` return the exact expected shape (2 Oddziały, 4 Stanowiska with correct `OddzialId` link values) - avoiding a repeat of PR #4's first-live-run column-name bugs.
- **Not verified**: the operator's real production database (different topology - see Out of Scope). The demo database's genuine multi-Oddzial/multi-Stanowisko structure was sufficient to exercise every code path this issue touches, but a final pass against production is recommended before the operator relies on this in a live invoicing flow.
