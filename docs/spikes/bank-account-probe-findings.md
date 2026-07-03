# Phase 0 live-probe findings: bank account / payment method per invoice (issue #1)

**Date**: 2026-07-02
**Environment**: Subiekt nexo PRO demo, deployment `Demo_1269000381e084a6bb1f8d36d8c` (Sfera 60.1.1.9292), DB `Nexo_Demo_1` on `localhost\INSERTNEXO`, login `Szef`.
**Tool**: `bridge/tools/BankAccountProbe` (throwaway console; subcommands `explore`, `baseline`, `service`, `transfer`, `cash`, `default-flag`).
**Evidence documents created in the demo DB**: FS 176/CENTRALA/2026 (baseline), FS 178 (transfer + explicit account), FS 179 (cash). Left in place as evidence; demo DB only.

Every plan question (Q1-Q5) is now answered live. The reflection dump was accurate on entity shapes but WRONG on two mechanism guesses (see 4 and 5).

## 1. Q1 - SQL schema for the account list (VERIFIED)

There is no `RachunkiBankowe` table. `RachunekBankowy` is a TPT subtype of "Centrum gromadzenia finansow":

- `ModelDanychContainer.CentraGromadzeniaFinansow` - base: `Id`, `Nazwa`.
- `ModelDanychContainer.CentraGromadzeniaFinansow_RachunekBankowy` - subtype: `Id` (=base Id), `Numer`, `NumerBanku`, `NumerBezSeparatorow`, `Opis`, `Aktywny`, `JestRachunkiemVAT`, `PodstawowyDlaWaluty`, `Waluta_Id` (-> `Waluty.Symbol`), `Wlasciciel_Id` (-> `Podmioty.Id`), `WlascicielPodstawowego_Id` (-> `Podmioty.Id`), `MojaFirmaId` (NULL for ordinary accounts; used only for tax/ZUS special roles).

**Seller scoping**: `Wlasciciel_Id = <Podmiot of MojaFirma>`. In the demo the MojaFirma Podmiot is the unique row with `Typ = 2 AND Podtyp = 11` ("Firma prezentacyjna", Id 100007). The table also contains ZUS/US accounts (`Wlasciciel_Id` = ZUS/US podmioty) and client accounts - the seller filter is mandatory.

**Default flag**: the UI's "Podstawowy" column is `WlascicielPodstawowego_Id IS NOT NULL` (a back-reference from `Podmiot.RachunekPodstawowy`), NOT `PodstawowyDlaWaluty` (all-false in the demo). The list endpoint should expose `isDefault = (WlascicielPodstawowego_Id IS NOT NULL)`.

Working list query:

```sql
SELECT rb.Id, cgf.Nazwa, rb.Numer, rb.NumerBanku, rb.Opis,
       w.Symbol AS Waluta, rb.Aktywny, rb.JestRachunkiemVAT,
       CASE WHEN rb.WlascicielPodstawowego_Id IS NOT NULL THEN 1 ELSE 0 END AS IsDefault
FROM ModelDanychContainer.CentraGromadzeniaFinansow_RachunekBankowy rb
JOIN ModelDanychContainer.CentraGromadzeniaFinansow cgf ON cgf.Id = rb.Id
LEFT JOIN ModelDanychContainer.Waluty w ON w.Id = rb.Waluta_Id
WHERE rb.Aktywny = 1
  AND rb.Wlasciciel_Id = (SELECT TOP 1 Id FROM ModelDanychContainer.Podmioty WHERE Typ = 2 AND Podtyp = 11)
ORDER BY IsDefault DESC, rb.Id;
```

Demo result: 100004 "Rachunek podstawowy" PLN (default), 100005 USD, 100006 EUR, 100007 "Rachunek on-line - testowy" PLN, 100008 VAT PLN.

## 2. Q5 - FormyPlatnosci vocabulary (VERIFIED)

`ModelDanychContainer.FormyPlatnosci`: `Id`, `Nazwa`, `Aktywna`, `TerminPlatnosci`, `TypPlatnosci_Id` (-> `TypyPlatnosci.Nazwa`), `RachunekBankowy_Id`, `PodstawowyRachunekBankowy`, `PodstawowyRachunekDlaWaluty`.

Stock install has `Gotówka` (Id 1, type "Gotówka") and `Przelew` (Id 2, type "Płatność odroczona", 7-day term, linked account 100004). D6 defaults (`gotówka`/`przelew`, case-insensitive) hold. `TypyPlatnosci` is a sturdier discriminator than the editable `Nazwa` - worth exposing in the config docs, but name-based config remains the chosen approach.

## 3. Q2/Q3 - per-invoice wiring mechanism (VERIFIED, differs from plan's default assumption)

Confirmed working sequence for an EXPLICIT payment selection (probe `transfer` -> FS 178, `cash` -> FS 179):

1. **Resolve the FormaPlatnosci entity inside the document's own unit of work by FK fixup**: set `Dane.FormaPlatnosciId = <id>`, then read `Dane.FormaPlatnosci` - EF fixup materializes the attached entity. (A facade `Znajdz(...)` entity belongs to a different ObjectContext and must not be assigned across contexts.)
2. **Add the payment row through the `IPlatnosciNaDokumencie` interface** (the BO implements these methods EXPLICITLY, so reflection lookup must go through the interface type, not the BO type):
   - transfer -> `DodajPlatnoscOdroczona(FormaPlatnosci)` - creates the deferred payment with the form's `TerminPlatnosci`;
   - cash -> `DodajPlatnoscNatychmiastowa(FormaPlatnosci)` - creates the immediate payment for the document amount.
3. **Set `Dane.FormaPlatnosci = <entity>`** so the document header carries the chosen form (read back as `Dokumenty.FormaPlatnosciId`).
4. **Transfer only - write the seller-account snapshot**: `Dane.RachunkiBankowe.RachunekBankowyMojejFirmy` is a `DaneRachunkuBankowego` snapshot (`Nazwa`, `Numer`); the `RachunekBankowyDokumentu` row is pre-attached on a fresh BO (1:1 with the document, shared PK). Set `Nazwa`/`Numer` to the chosen account's values.
5. Do NOT call `DodajPlatnosciDomyslne()` / `DodajDomyslnaPlatnoscNatychmiastowaNaKwoteDokumentu()` on the explicit path - see finding 5.

Read-back of FS 178 (transfer to non-default account 100007):

```
Dokumenty:                 FormaPlatnosciId = 2 (Przelew)
PlatnosciDokumentow:       KwotaPlatnosci = 123.00, RodzajPlatnosci = 3 (odroczona), TerminDni = 7, FormaPlatnosci_Id = 2
RachunkiBankoweDokumentow: RachunekBankowyMojejFirmy_Nazwa = 'Rachunek on-line - testowy'
                           RachunekBankowyMojejFirmy_Numer = '38 2490 0005 7898 4745 0552 5035'
```

Baseline (no selection, FS 176) stamps the DEFAULT account snapshot ('Rachunek podstawowy') automatically - so the no-selection path needs no snapshot handling.

## 4. Dead end: `AktualizujRachunkiBankowe(string[], out string)` (do NOT use)

Despite the promising name, this BO method updates the BUYER's (client's) bank accounts (`RachunekBankowyKlienta` path). Called with a seller account number it fails with "Istnieje już rachunek bankowy o tym samym numerze u innego klienta (Firma prezentacyjna ...)". Not the seam for this feature.

## 5. Behavioral finding: `DodajPlatnosciDomyslne()` ignores `Dane.FormaPlatnosci`

The defaults derive from configuration (document type / sales point / kontrahent), not from a pre-set `Dane.FormaPlatnosci` - setting Przelew and then calling the defaults still produced Gotówka rows. This is WHY the explicit path must add payments itself (finding 3) instead of steering the defaults.

Additionally, with one demo kontrahent ("ABC s.c.", Id 100026) `DodajPlatnosciDomyslne()` emitted a stray ZERO-amount second `PlatnoscDokumentu` row that fails entity validation (`KwotaPlatnosci: Wartość w polu musi być większa od 0`) and silently vetoes `Zapisz()` (returns false, empty `InvalidData` message surface at document level). Existing-bridge hardening opportunity (out of scope for #1, candidate follow-up issue): drop zero-amount payment rows before save via `IPlatnosciNaDokumencie.Usun(...)`.

## 6. Q4 - stretch goal: flipping the default account (VERIFIED, works)

The default lives on the PODMIOT side: `Podmiot.RachunekPodstawowy` (nav; DB back-reference `WlascicielPodstawowego_Id` on the account row). Confirmed:

- Writing `RachunekBankowy.WlascicielPodstawowego` directly on the account entity throws `UnsponsoredModificationException` - the RachunekBankowy BO does not sponsor that field. Dead end.
- Working path: load the seller Podmiot's business object via `IPodmioty.Znajdz(x => x.Id == ownerId)` (`InsERT.Moria.Klienci.IPodmioty`), locate the target account entity in `podmiot.Dane.Rachunki` (collection property is named `Rachunki`, not `RachunkiBankowe`), set `Dane.RachunekPodstawowy = <account>`, `Zapisz()`. The previous default's back-reference clears automatically (verified in SQL), and the operation is cleanly reversible the same way. `PodstawowyDlaWaluty` stays untouched - the per-currency flag is a separate concept the feature does not need.

D7 stays IN scope: `PUT /api/bank-accounts/{id}/default` maps to this Podmiot-BO write, executed on the Sfera write queue.

## 7. Operational gotchas hit while probing (worth keeping in mind for tests/docs)

- **`Zapisz() == false` with `Waliduj()` passing and empty InvalidData happens when the position's product has zero stock on the DOCUMENT's warehouse** (default MAG/100000) - stock on other warehouses does not help, and the bridge's `IgnorujBlokade*` flags do not bypass it. The bridge's generic error hint ("typowo blokada rozchodu/stanu magazynowego") is accurate. Test fixtures must pick products with stock on the document warehouse.
- `Nexo_KSEF Test` DB is not Sfera-capable (no Subiekt PRO product registered) and is one schema version ahead (61.x) of the demo deployment binaries (60.x). All Sfera work targets `Nexo_Demo_1`.
- JIT trap: any Sfera type referenced directly in the top-level `Main` body loads before `SferaBoot.InstallAssemblyResolver` runs - keep Sfera-typed code in local functions (same pattern the bridge's hosted services follow).
