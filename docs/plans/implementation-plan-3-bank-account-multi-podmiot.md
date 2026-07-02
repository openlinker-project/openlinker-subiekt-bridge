# Implementation Plan: Bank-account endpoints assume a single seller Podmiot (TOP 1)

**Date**: 2026-07-02
**Status**: Ready for Review
**Issue**: openlinker-project/openlinker-subiekt-bridge#3
**Branch**: `3-bank-account-multi-podmiot`
**Estimated Effort**: 0.5-1 day for the mechanical fix in this PR; the invoice-contract Oddział/Płatnik selector is a separate follow-up gated on a live topology probe (see §5).

---

## 1. Task Summary

**Objective**: Stop `GET /api/bank-accounts` (and the default-account writer / explicit-payment issuance path) from silently dropping bank accounts that belong to any seller `Podmiot` other than the first one returned by `TOP 1`.

**Context**: Issue #1 (PR #2) shipped bank-account listing and explicit payment selection scoped with:
```sql
WHERE rb.Wlasciciel_Id = (SELECT TOP 1 Id FROM ModelDanychContainer.Podmioty WHERE Typ = 2 AND Podtyp = 11)
```
The operator's real install has multiple distinct płatnicy/oddziały. `TOP 1` picks one arbitrarily; every account belonging to any other seller `Podmiot` disappears from the list with no error, and neither the default-account writer nor the invoice-payment pre-check can resolve those accounts either.

**Classification**: Infrastructure (SQL reader) + Integration (Sfera adapters, pre-check queries) + Interface (API response shape).

### What this PR fixes vs. defers

The issue's own text calls the full redesign "a genuinely open design question, not a small patch" and requires a live SQL/Sfera probe against the operator's real database to decide between two topologies (multiple genuinely-separate `MojaFirma` Podmioty vs. one Podmiot with multiple `Oddzial` branches) before the wire-contract shape (an Oddział/Płatnik selector on `CreateInvoiceRequestDto`) can be designed. This repo has no `dotnet`/SQL Server/live Sfera access from this environment (same constraint as the hexagon refactor — see `docs/WINDOWS_HANDOFF.md`), so that probe cannot run in this session.

This PR scopes to the part that is **not** topology-dependent and is safe to ship without the live probe:

- Every `Podmiot` of `Typ = 2 AND Podtyp = 11` is a legitimate seller entity regardless of which topology applies (multi-Podmiot or Podmiot-with-Oddziały both have `Typ=2/Podtyp=11` rows for whichever level owns bank accounts directly). Widening the three `TOP 1` sites to `IN (...)` removes the silent-truncation bug with zero risk of removing a bank account that a caller should be able to see — it can only surface previously-hidden accounts.
- Tagging each returned account with its owning Podmiot id/name lets an OL-side caller (or the operator via the cockpit) already tell accounts apart by payer, satisfying acceptance-criterion "enumerates all payers/branches" without yet requiring the invoice-contract selector.

Deferred to a follow-up issue once the live probe runs (tracked as an open item on #3, not closed by this PR):
- `CreateInvoiceRequestDto` Oddział/Płatnik selector.
- Threading that selector through to bank-account resolution and `SferaDefaultBankAccountWriter`'s scope-of-default.
- The multi-Podmiot fixture test in `bridge/tests/` (needs a live/SQL-Server-backed test harness that doesn't exist yet — `Subiekt.Bridge.Infrastructure.Sql` has zero existing tests because Dapper-over-`ModelDanychContainer` T-SQL doesn't run against SQLite).

---

## 2. Scope & Non-Goals

### In Scope
- `SqlBankAccountsReader.ListAsync` — enumerate every seller `Podmiot`, not `TOP 1`; return the owning Podmiot's id + name per account (additive fields on `BankAccountView`).
- `SferaRachunkiBankoweService.UstawRachunekPodstawowy` pre-check query — same `IN (...)` widening (the ownership resolution + Sfera-side write logic per row is already correct; only the "does this account belong to some seller" gate was too narrow).
- `SferaDokumentySprzedazyService.ApplyExplicitPayment` transfer pre-check query — same widening.
- `GET /api/bank-accounts` response — surface `ownerPodmiotId` / `ownerName` per account.
- `bridge/tools/BankAccountProbe` — add a `podmioty` subcommand that dumps `Podmioty` (Typ=2/Podtyp=11) and any `JednostkaOrganizacyjna`/`Oddzial` rows, for the operator/Windows session to run against the real database as the still-pending live-topology probe.
- `docs/API_ENDPOINTS.md` — document the new response fields.

### Out of Scope (this PR)
- Any Oddział/Płatnik selector on the invoice-issuance contract.
- Grouping/filtering the bank-account list by an explicit selector (no selector exists yet).
- OpenLinker-side (`openlinker-project/openlinker#1324`) changes — that plan already documents "1 bridge = 1 payer" as an unverified MVP assumption pending this issue and is intentionally not touched here.
- New automated tests requiring a live SQL Server / Sfera connection (none of the three changed queries can run in this dev environment; verification happens on the Windows/Subiekt machine per the existing handoff pattern).

### Constraints
- **No regression for the single-Podmiot case**: when only one seller Podmiot exists (today's assumption, and today's only tested case), `IN (SELECT Id FROM Podmioty WHERE Typ=2 AND Podtyp=11)` returns the exact same single-row set as `TOP 1` — behavior is byte-for-byte identical.
- Reads stay on the 3A `IBankAccountsReader` port; writes stay on `SferaWriteQueue`. No new port shape needed for this scoped fix.
- `BankAccountView` gains fields, it does not remove any — additive contract change.

---

## 3. Architecture Mapping

| Layer | Project | What changes |
|---|---|---|
| Application | `Subiekt.Bridge.Application` | `BankAccountView` gains `OwnerPodmiotId` (int) + `OwnerName` (string?) |
| Infrastructure.Sql | `Subiekt.Bridge.Infrastructure.Sql` | `SqlBankAccountsReader.ListSql` widened + selects owner columns |
| Infrastructure.Sfera | `Subiekt.Bridge.Infrastructure.Sfera` | `SferaRachunkiBankoweService` + `SferaDokumentySprzedazyService` pre-check queries widened |
| Api | `Subiekt.Bridge.Api` | `BankAccountsEndpoints` GET projection surfaces the two new fields |
| tools | `bridge/tools/BankAccountProbe` | new `podmioty` subcommand (Windows-run, not part of the shipped bridge) |

**Boundary justification**: this is a pure infrastructure-layer correction (a SQL predicate was wrong) plus an additive read-model field; no Domain rule or Application port shape changes, so `PaymentSelection`/`SalesDocument` are untouched.

---

## 4. Step-by-Step Plan

1. `Subiekt.Bridge.Application/Ports/IBankAccountsReader.cs` — add `OwnerPodmiotId` (int) and `OwnerName` (string?) to `BankAccountView`.
2. `Subiekt.Bridge.Infrastructure.Sql/SqlBankAccountsReader.cs` — rewrite `ListSql`:
   ```sql
   SELECT rb.Id, cgf.Nazwa, rb.Numer, rb.NumerBanku, rb.Opis, w.Symbol AS Waluta,
          rb.JestRachunkiemVAT,
          CAST(CASE WHEN rb.WlascicielPodstawowego_Id IS NOT NULL THEN 1 ELSE 0 END AS bit) AS IsDefault,
          rb.Wlasciciel_Id AS OwnerPodmiotId,
          owner.Nazwa AS OwnerName
   FROM ModelDanychContainer.CentraGromadzeniaFinansow_RachunekBankowy rb
   JOIN ModelDanychContainer.CentraGromadzeniaFinansow cgf ON cgf.Id = rb.Id
   LEFT JOIN ModelDanychContainer.Waluty w ON w.Id = rb.Waluta_Id
   LEFT JOIN ModelDanychContainer.Podmioty owner ON owner.Id = rb.Wlasciciel_Id
   WHERE rb.Aktywny = 1
     AND rb.Wlasciciel_Id IN (SELECT Id FROM ModelDanychContainer.Podmioty WHERE Typ = 2 AND Podtyp = 11)
   ORDER BY rb.Wlasciciel_Id, IsDefault DESC, rb.Id;
   ```
   Update the class doc-comment to describe the multi-Podmiot enumeration.
3. `Subiekt.Bridge.Infrastructure.Sfera/SferaRachunkiBankoweService.cs` — widen the pre-check `WHERE` clause the same way (`IN (...)` instead of `= (SELECT TOP 1 ...)`); no other logic changes (it already resolves `ownerId` per-row from the query result and looks up that specific Podmiot's business object, which is already topology-correct).
4. `Subiekt.Bridge.Infrastructure.Sfera/SferaDokumentySprzedazyService.cs` (`ApplyExplicitPayment`, transfer pre-check around line 312) — same `IN (...)` widening.
5. `Subiekt.Bridge.Api/Endpoints/BankAccountsEndpoints.cs` — add `ownerPodmiotId` / `ownerName` to the projected JSON.
6. `bridge/tools/BankAccountProbe/Program.cs` — add a `podmioty` subcommand: raw-SQL dump of `Podmioty` (Typ=2/Podtyp=11) and a best-effort dump of any `JednostkaOrganizacyjna`/`Oddzial`-named table/columns (via `SferaInspect`-style reflection or a schema query), so a future session on the Windows machine can run it against the real DB and settle the topology question needed for the invoice-contract selector.
7. `docs/API_ENDPOINTS.md` — document the two new `GET /api/bank-accounts` response fields.
8. Update issue #3 (via PR body, not a separate comment) noting the scoped fix + the still-open Oddział/Płatnik selector work item.

## 5. Deferred Work (tracked, not closed by this PR)

- Live probe (`podmioty` subcommand output) against the operator's real database — needs the Windows/Sfera handoff session.
- `CreateInvoiceRequestDto` Oddział/Płatnik selector + `SferaDefaultBankAccountWriter` default-scope-per-payer — designed only after the probe confirms multi-Podmiot vs. Podmiot-with-Oddziały.
- Multi-Podmiot fixture tests in `bridge/tests/` — needs either a SQL-Server-compatible test harness or to move to an integration-test tier that isn't part of this repo yet.

## 6. Validation

- No `dotnet`/SQL Server available in this environment; this PR is authored and self-reviewed on WSL, matching the existing `docs/WINDOWS_HANDOFF.md` pattern — build + live verification of the widened queries happens in a follow-up Windows session against a multi-Podmiot (or single-Podmiot, for regression) database.
- Self-review checklist: single-Podmiot behavior is provably unchanged (`IN (SELECT ... TOP 1 equivalent set)` when only one row exists); no Domain/Application port signature broke; `BankAccountView` change is additive so existing serialization call sites compile unchanged.
