# Issue #3 / #5 Phase 1 — live topology probe findings

**Date**: 2026-07-02
**Database**: `Nexo_Demo_1` on `localhost\INSERTNEXO` (demo deployment `Demo_1269000381e084a6bb1f8d36d8c`)
**Tool**: `bridge/tools/BankAccountProbe podmioty` (extended this session — see git history)

This is a demo database, not the operator's real multi-payer production install — it does **not**
reproduce the multi-Podmiot scenario issue #3 was filed for (this DB has exactly one seller Podmiot).
It does, however, reproduce a genuine multi-Oddział / multi-Stanowisko-Kasowe topology, which is
exactly the data issue #5 needs. Two real bugs were found and fixed along the way (see "Bugs found"
below) — this demo DB was the **first live run** of code shipped in PR #4 and of the `podmioty`
probe subcommand added in that same PR; neither had been executed before.

## 1. Seller Podmioty (Typ=2, Podtyp=11)

```
Podmiot Id=100007 NazwaSkrocona="Firma prezentacyjna" Typ=2 Podtyp=11
```

**Exactly one.** On this database, PR #2's original `TOP 1` assumption would have been correct — the
multi-payer bug it fixed doesn't manifest here. This does **not** invalidate issue #3/PR #4; it just
means this demo DB can't be used to positively verify the multi-Podmiot fix. No `Nadrzedn*`/`Rodzic*`/
`Oddzial*`/`Platnik*`-named column exists directly on `Podmioty` (confirmed via
`INFORMATION_SCHEMA.COLUMNS`) — a Podmiot does not reference an Oddział or a parent Podmiot inline.

## 2. Oddziały (`ModelDanychContainer.JednostkiOrganizacyjne_Oddzial`)

```
Oddzial Id=100001 Nazwa="Pachnidło"        Centrala_Id=100000 PodstawowyRachunekBankowy_Id=NULL
Oddzial Id=100002 Nazwa="Centrum Handlowe" Centrala_Id=100000 PodstawowyRachunekBankowy_Id=NULL
```

**Two Oddziały exist, independent of the (single) seller Podmiot count.** This confirms Oddział and
Płatnik/Podmiot are **distinct concepts** — a single-payer install can still have multiple branches.
Both Oddziały share `Centrala_Id=100000`, which is a *different* organizational-unit id not itself
present in this table — `100000` is the head-office unit (see §3, it owns the one linked Stanowisko
Kasowe). `Centrala_Id` is a plain FK column (not `NULL`-able in the data, always `100000` here), so
every Oddział in this install belongs to exactly one Centrala. Each Oddział *can* carry its own
`PodstawowyRachunekBankowy_Id` (both are `NULL` here — no branch-level default account configured on
this demo data, but the schema clearly supports one, independent of the Podmiot-level
`RachunekPodstawowy` from issue #3).

## 3. Stanowiska Kasowe (`ModelDanychContainer.CentraGromadzeniaFinansow_StanowiskoKasowe`)

```
StanowiskoKasowe Id=100065 Nazwa="Kasa Centralna"              Symbol=CENTR  Oddzial_Id=100000
StanowiskoKasowe Id=100066 Nazwa="Kasa Outlet"                 Symbol=OUTLET Oddzial_Id=NULL (unlinked)
StanowiskoKasowe Id=100067 Nazwa="Kasa Galaxia 1"              Symbol=GALAX1 Oddzial_Id=NULL (unlinked)
StanowiskoKasowe Id=100068 Nazwa="Kasa Galaxia 2 (wielowalutowa)" Symbol=GALAX2 Oddzial_Id=NULL (unlinked)
```

(joined via `ModelDanychContainer.StanowiskoKasoweJednostkaOrganizacyjna`, a link table with columns
`JednostkiOrganizacyjne_Id` / `StanowiskaKasowe_Id`.)

**Revises the assumption in issue #5's body.** The reflection-dump evidence
(`StanowiskoKasoweZInnejJednostkiOrganizacyjnejBlad`) was read as "a Stanowisko Kasowe belongs to
exactly one Oddział" — the live link table shows this is **not a mandatory 1:1 FK**: only 1 of 4
Stanowiska Kasowe has a link-table row at all (`Kasa Centralna` → Centrala `100000`, itself an
organizational unit, not one of the two `Oddzial` rows from §2). The other three
(`Outlet`/`Galaxia 1`/`Galaxia 2`) have **no row in the link table** — unlinked, not linked-to-null.
Revised reading: the link table is how an operator **optionally restricts** a Stanowisko Kasowe to a
specific Jednostka Organizacyjna; an unlinked Stanowisko is presumably usable from any Oddział, and
the `...ZInnejJednostkiOrganizacyjnejBlad` error fires only when a document's Oddział conflicts with
an *explicit* link-table row, not for every stanowisko. **This needs one more live check before
issue #5's wire-contract design locks in**: attempt issuing a document under Oddział `100001`
("Pachnidło") using the *linked* `Kasa Centralna` (Oddział `100000`) to confirm the error actually
fires cross-Oddział, and using an *unlinked* stanowisko (e.g. `Kasa Outlet`) to confirm it's
accepted from any Oddział. Not done in this session — requires actually issuing throwaway
transactions, which is a bigger blast-radius action than a read-only probe; left for issue #5's
implementation phase.

`CentraGromadzeniaFinansow_StanowiskoKasowe`'s own columns (`DomyslnaFormaPlatnosci_Id`,
`FormaDomyslnaNaDokumencieSprzedazy_Id`, `FormaDomyslnaNaDokumencieZakupu_Id`, `Kasa_Id`,
`KasaDlaKtorejDomyslne_Id`, `Symbol`, `Opis`) carry no `Nazwa` — like `RachunekBankowy`, it's a
`CentraGromadzeniaFinansow` TPT subtype, so the display name (`Kasa Centralna`, etc.) lives on the
base `CentraGromadzeniaFinansow.Nazwa` row, joined by `Id`.

## 4. Bugs found and fixed this session (first live run of PR #4 + the probe)

1. **`Podmioty` has no `Nazwa` column.** PR #4's `SqlBankAccountsReader.ListSql` (`owner.Nazwa AS
   OwnerName`) and this probe's original `podmioty` queries both assumed one — both threw
   `Invalid column name 'Nazwa'` on the very first live run. Fixed to `NazwaSkrocona` (the actual
   128-column schema, confirmed via `INFORMATION_SCHEMA.COLUMNS`, has no plain `Nazwa`).
2. **`Podmioty.Typ`/`Podmioty.Podtyp` are `tinyint`, not `int`.** `SqlDataReader.GetInt32` throws
   `InvalidCastException` on a `tinyint` (boxes as `System.Byte`). Fixed by widening every uncertain
   numeric read in the probe through a `Convert.ToInt32(r.GetValue(i))` helper (`AnyInt`) instead of
   guessing column widths per-query.

Both fixes are in `bridge/Subiekt.Bridge.Infrastructure.Sql/SqlBankAccountsReader.cs` (production
code, ships in PR #4) and `bridge/tools/BankAccountProbe/Program.cs` (throwaway tool). The production
fix matters more: **PR #4's `GET /api/bank-accounts` `ownerName` field would have thrown a 500 on
its first real invocation against any live database**, demo or production, multi-payer or not — this
was not multi-payer-specific, it was a schema-assumption bug in code that had never actually run
against Sfera before this session.

## 5. Open items for issue #5's wire-contract design (unresolved by this probe)

- Whether "Płatnik" (the operator's term) maps to `Podmiot` (issue #3's model) or `JednostkaOrganizacyjna`/Oddział — this demo data suggests they're independent axes (1 Podmiot, 2 Oddziały), so the operator's real install likely needs **both** a payer selector (Podmiot, if truly multi-payer) **and** a branch selector (Oddział) as separate fields, not one collapsed concept.
- The cross-Oddział rejection behavior for Stanowisko Kasowe (see §3) needs a live write-side check, not just a read-side probe.
- `StanowiskoKasoweWymaganeDlaDokumentowZPlatnosciamiNatychmiastowymiBlad` (cash documents require a Stanowisko Kasowe) still needs to be traced to whichever implicit default `SferaDokumentySprzedazyService`'s existing Cash-issuance path resolves today — not done this session (requires reading/instrumenting the issuance code path, not just the read-only schema).
