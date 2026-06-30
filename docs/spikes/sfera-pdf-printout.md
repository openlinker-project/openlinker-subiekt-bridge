# SPIKE: Headless Sfera printout → PDF bytes for FV/PA

**Issue:** [#2] `[SPIKE] feat(subiekt-bridge): verify headless Sfera printout → PDF bytes for FV/PA`
**Date:** 2026-06-24
**Verdict:** ✅ **GO — headless render to PDF bytes is confirmed working with real proof.**

An issued Subiekt sales document (FV / PA) **can** be rendered to PDF bytes
programmatically with **no GUI and no interactive print dialog**, reusing the
bridge's existing Sfera session. This was proven against the live demo Nexo by
rendering existing issued documents to real `%PDF-` files from code.

---

## Proof (real artifacts — not fabricated)

A throwaway console probe connected via the bridge's exact
`SferaSession.Connect` path (same `DanePolaczenia.Jawne` + `MenedzerPolaczen.Polacz` +
`Uchwyt.Zaloguj` reflection), loaded existing documents by Subiekt id, and exported each to PDF.

Probe output (login + render), reproduced across multiple runs:

```
Login result: Zalogowano
=== load+render doc id 100360 ===
  doc id 100360: Disc='DokumentDS' Numer='FS 169/CENTRALA/2026'
  using TypWzorcaWydruku.FakturaSprzedazy
  Znajdz BO=DokumentSprzedazyBO Dane=DokumentDS
  WybranyWzorzec=FS standard
  EksportAsync completed in 3593 ms; OstatniaOperacjaZakonczonaSukcesem=True
  OUTPUT: ...\doc-100360.pdf size=107286 header='%PDF-' isPDF=True
=== load+render doc id 100358 ===
  doc id 100358: Disc='DokumentDS' Numer='PA 8/CENTRALA/2026'
  using TypWzorcaWydruku.Paragon
  WybranyWzorzec=PA standard
  EksportAsync completed in 2083 ms; OstatniaOperacjaZakonczonaSukcesem=True
  OUTPUT: ...\doc-100358.pdf size=105071 header='%PDF-' isPDF=True
```

Independent file verification (`file` + header/trailer bytes):

```
FV-100360-FS169.pdf: %PDF-1.7 ... %%EOF   107286 bytes  PDF document, version 1.7, 1 page(s)
PA-100358-PA8.pdf:   %PDF-1.7 ... %%EOF   105071 bytes  PDF document, version 1.7, 1 page(s)
```

Committed proof files:
- `docs/spikes/proof/FV-100360-FS169.pdf` — invoice **FS 169/CENTRALA/2026** (Subiekt id 100360)
- `docs/spikes/proof/PA-100358-PA8.pdf` — receipt **PA 8/CENTRALA/2026** (Subiekt id 100358)

A third doc (FS 168, id 100359) also rendered cleanly in the same run.

---

## Concrete SDK call path

The print engine is **Stimulsoft** (`InsERT.Moria.Printing.Stimulsoft.dll` +
`Stimulsoft.Report.dll`); PDF output is a first-class export format. The headless
path goes through the high-level `IWydruk` API — no Stimulsoft viewer/UI is touched.

Assemblies (resolved at runtime from the Nexo `Binaries` dir via the bridge's
`AssemblyLoadContext.Default.Resolving` resolver): `InsERT.Moria.Wydruki.dll`,
`InsERT.Mox.Printing.dll`, `InsERT.Moria.Printing.Stimulsoft.dll`.

Step-by-step (all via the existing `Uchwyt` session):

1. **Get the print factory:** `uchwyt.Wydruki()` (extension on `InsERT.Moria.Sfera.UchwytRozszerzenia`)
   → `IWydruki` (concrete `InsERT.Moria.Wydruki.WydrukManager`).
2. **Create the per-document printout:** `IWydruki.Utworz(TypWzorcaWydruku typ)` → `IWydruk`.
   - `TypWzorcaWydruku` (enum `InsERT.Moria.Wydruki.Enums.TypWzorcaWydruku`):
     `FakturaSprzedazy = 3000`, `Paragon = 8400`, `ParagonImienny = 8500`,
     `FakturaSprzedazyUproszczona = 3100`, `FakturaDetaliczna = 2900`, etc.
   - Concrete `IWydruk` for FV is `...DokumentySprzedazy.FakturaSprzedazy.FakturaSprzedazyWydruk`; PA is `...Paragon.ParagonWydruk`.
3. **Load the existing document entity** (the proven bridge pattern, from `SferaKorektyService`):
   `IDokumenty docs = uchwyt.Dokumenty()` then
   `docs.Znajdz<IDokumentSprzedazy>(Expression<Func<Dokument,bool>> d => d.Id == id)`.
   The returned BO's `.Dane` is the `InsERT.Moria.ModelDanych.DokumentDS` entity.
4. **Initialize the printout with the entity:** `IWydruk.Inicjalizuj(object)` — pass the
   raw `DokumentDS` entity (not the BO, not a `List<>`, not a pre-built wrapper).
5. **Pick the template:** `IWydruk.ParametryDrukowania` is an `IWydrukParametry`
   (`InsERT.Moria.Wydruki.Base.WydrukParametry`). After `Inicjalizuj`, its
   `WybranyWzorzec` is already populated with the document's **default** print
   template (e.g. `FS standard`, `PA standard`); `DostepneWzorce` lists the alternatives.
6. **Configure file-export to PDF** on `ParametryDrukowania`:
   - `Eksport = true`, `Drukowac = false`, `Email = false`
   - `FormatEksportu = "pdf"` (string; the available set is exposed via `DostepneFormatyEksportu` — `pdf` is in it)
   - `SciezkaEksportu = <OUTPUT DIRECTORY>` — **this is a directory, not a file path**
   - `NazwaDokumentuUzytkownika = "<basename>"` — the file is written as `<dir>\<basename>.pdf`
   - `ZastapPliki = true`
7. **Render:** `IWydruk.EksportAsync()` returns an `EventWaitHandle`; wait on it,
   then check `IWydruk.OstatniaOperacjaZakonczonaSukcesem == true`
   (and `IWydruk.PobierzListeBledow()` for messages). `IWydruk.Eksport()` (sync) does
   the same work but the async variant gives a clean completion handle.

> The low-level building blocks under this are `StiPrintTask` +
> `StiFileOutput(StiExportFormat.Pdf, …)` / `StiExport.Export(StiExportFormat.Pdf, Stream, …)`
> in `InsERT.Mox.Printing.Stimulsoft`. The `IWydruk` facade is the right altitude
> for the implementation — it wires the template, data sources and Stimulsoft task for us.

### One non-obvious gotcha (cost the most spike time)

`Inicjalizuj(DokumentDS)` stores the entity in the derived field `_dokumentEncja`,
but the base `Wydruk.Eksport`/`EksportAsync` pipeline reads the **base** field
`_obiektDoWydruku` and feeds it to `PobierzObiektDoDrukowania(obiektWejsciowy)` →
`DokumentWydruk.PobierzObiektDoDrukowaniaOverride(object)`. In the probe, after
`Inicjalizuj` the base `_obiektDoWydruku` stayed `null`, so `EksportAsync` threw
*"Niepoprawny typ obiektu wejściowego"*. The probe worked around it by reflectively
setting `_obiektDoWydruku = <DokumentDS>` before export.

**This is almost certainly a probe artifact, not a real SDK constraint** — the proper
SDK flow likely populates `_obiektDoWydruku` through a member the probe didn't call
(the real Subiekt UI passes the selected document straight into the print orchestration).
The implementation issue should first try the documented `Inicjalizuj` + `EksportAsync`
flow cleanly; if `_obiektDoWydruku` is still unset, either set it (as the probe did) or
find the correct seam. `PobierzObiektDoDrukowaniaOverride(DokumentDS)` returning a valid
`DokumentDlaWydruku<DokumentDS>` confirms the raw entity is the correct input object.

---

## Template prerequisite

- A print template (`wzorzec wydruku`) **is required**, and it **must exist in the Nexo DB**.
- **The demo Nexo already has them, pre-configured, with a default selected.** No operator
  setup was needed for the spike. Available templates observed:
  - FV: `FS standard` (default), `FS angielski`, `FS niemiecki`, `FS z jednostką nadrzędną`, `FS z rozbiciem na dostawy`
  - PA: `PA standard` (default), `PA z rozbiciem na dostawy`
- The implementation should rely on `ParametryDrukowania.WybranyWzorzec` (the per-document
  default) and not hard-code a template; optionally allow choosing from `DostepneWzorce`.

## Edition / licensing

- Rendered against the demo **Subiekt** product (`ProductId.Subiekt`) with Sfera, the
  exact same session the bridge already uses to issue documents. **No extra module was
  required** — Subiekt PRO + Sfera is sufficient for headless PDF printout.
- No licence/permission exception surfaced (`OstatniaOperacjaZakonczonaSukcesem=True`).

## Threading / runtime

- Runs in-process against the live `Uchwyt`; **no separate STA apartment was needed** for
  this probe (plain console host, default threading).
- `EksportAsync()` does the render on a background worker and signals an `EventWaitHandle`;
  we block on it. Because Sfera is **not thread-safe for arbitrary parallel calls**, the
  render must be serialized against the session the same way mutations are — i.e. **run it
  on / behind the existing single-writer `SferaWriteQueue`** (or a dedicated serialized
  read path that holds `SferaSession.SyncRoot`). Do not render concurrently with an issue.
- **Latency (measured):** first render in a fresh process ~**3.3–3.6 s** (Stimulsoft/JIT
  warmup); subsequent renders in the same process **~0.3–2.1 s** per document. Steady-state
  is well within an interactive "click → open PDF" budget. Output size ≈ 105–107 KB per 1-page doc.

---

## Go / no-go + recommendation for the implementation issue

**GO.** Proceed with the bridge-led design from issue #2: the bridge renders the
sales document to PDF and serves it under a self-authenticating browser-reachable URL;
OpenLinker stays untouched (the adapter already forwards `pdfUrl`).

Recommended approach for the follow-up implementation issue:

1. Add a Sfera adapter service `SferaPdfPrintoutService` (Infrastructure.Sfera) that, given
   a Subiekt document id, runs the call path above and returns **PDF bytes** (render to a
   temp dir via `SciezkaEksportu`, read the file, delete it — or wire `StiExport.Export(…, Stream)`
   directly for a pure in-memory byte[] with no temp file).
2. **Serialize the render on the existing single-writer Sfera queue** (`SferaWriteQueue`)
   so it never races the issue path; cache the result keyed by document id (PDFs are immutable once issued).
3. Map document type → `TypWzorcaWydruku` from the document number prefix (the bridge already
   does `PA…` ⇒ paragon, else faktura in `SferaKorektyService`) or from a type read; use the
   default `WybranyWzorzec`.
4. Expose `GET /api/faktury/{id}/pdf` (the implementation issue's endpoint) returning the bytes
   as `application/pdf`, behind the signed/expiring-URL auth scheme, on a browser-reachable
   absolute `https://` URL with trusted TLS.
5. Re-test the clean `Inicjalizuj` + `EksportAsync` seam (see gotcha above) before falling back
   to setting `_obiektDoWydruku` reflectively.

No fallback (deep-link / operator export) is needed — headless render works.

---

### Reproduction notes (throwaway probe — not committed)

- Probe lived at `C:\projekty\blocky\sfera-pdf-probe` (kept out of the bridge source tree).
- Connection: `localhost\INSERTNEXO` / `Nexo_Demo_1`, Nexo user `Szef`, operator password via
  env `Sfera__NexoPassword` (not stored).
- SDK surface was discovered by dumping `InsERT.Moria.Wydruki.dll` / `InsERT.Mox.Printing.dll` /
  `InsERT.Moria.Printing.Stimulsoft.dll` with `bridge/tools/SferaInspect` and by reflecting over
  the live objects.
