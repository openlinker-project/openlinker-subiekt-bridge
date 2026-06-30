using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Subiekt.Bridge.Infrastructure.Sfera;

/// <summary>
/// Moved verbatim (move-and-wrap) from the legacy
/// <c>SferaApi.Services.DokumentySprzedazyService</c>. Business logic is unchanged;
/// structural changes only: runs synchronously against a passed-in
/// <see cref="SferaSession"/> (no Task.Run/lock — <see cref="SferaWriteQueue"/>
/// serializes), uses the shared <see cref="SferaObjectAccessor"/> instead of the
/// duplicated helpers, and takes <see cref="SferaInvoiceInput"/> instead of the Api DTO.
/// </summary>
public sealed class SferaDokumentySprzedazyService
{
    private readonly ILogger<SferaDokumentySprzedazyService> _log;
    private readonly SferaObjectAccessor _acc;

    public SferaDokumentySprzedazyService(ILogger<SferaDokumentySprzedazyService> log)
    {
        _log = log;
        _acc = new SferaObjectAccessor(log);
    }

    public (int Id, string Numer) UtworzFaktura(SferaSession sfera, SferaInvoiceInput dto)
        => Create(sfera, "UtworzFaktureSprzedazy", dto);

    public (int Id, string Numer) UtworzParagon(SferaSession sfera, SferaInvoiceInput dto)
        => Create(sfera, "UtworzParagon", dto);

    private (int Id, string Numer) Create(SferaSession sfera, string factoryMethod, SferaInvoiceInput dto)
    {
        var uchwyt = sfera.Uchwyt;
        var conn = uchwyt.PodajPolaczenie();
        if (conn.State != System.Data.ConnectionState.Open)
            conn.Open();

        // 1. Validate kontrahent exists.
        using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "SELECT COUNT(*) FROM ModelDanychContainer.Podmioty WHERE Id = @id";
            checkCmd.Parameters.AddWithValue("@id", dto.KontrahentId);
            if ((int)checkCmd.ExecuteScalar() == 0)
                throw new ArgumentException($"Kontrahent ID {dto.KontrahentId} not found");
        }

        // 2. Create the document BO.
        var iDokumentyType = SferaReflection.RequireType("InsERT.Moria.Dokumenty.Logistyka.IDokumentySprzedazy, InsERT.Moria.Dokumenty.Logistyka");
        var facade = uchwyt.PodajObiektTypu(iDokumentyType);
        var factory = iDokumentyType.GetMethod(factoryMethod, SferaObjectAccessor.Flags)
            ?? throw new InvalidOperationException($"IDokumentySprzedazy.{factoryMethod}() not found");
        var bo = factory.Invoke(facade, null)
            ?? throw new InvalidOperationException($"{factoryMethod}() returned null");
        var boType = bo.GetType();

        var dane = boType.GetProperty("Dane", SferaObjectAccessor.Flags)?.GetValue(bo)
            ?? throw new InvalidOperationException("Document BO.Dane is null");
        var daneType = dane.GetType();

        // Bypass soft blocks that stop Zapisz() returning false in a POC/demo.
        _acc.SetBoolFlag(bo, boType, "IgnorujBlokade", true);
        _acc.SetBoolFlag(bo, boType, "WylaczBlokowanieStanowPrzezRezerwacjeIlosciowa", true);
        _acc.SetBoolFlag(bo, boType, "NieSprawdzajRealizacji", true);

        // 3. Set the buyer using the document BO's OWN method, so the Podmiot is
        //    resolved inside the document's UnitOfWork.
        var ustawNabywce = boType.GetMethod("UstawNabywceWedlugId", SferaObjectAccessor.Flags, new[] { typeof(int) })
            ?? throw new InvalidOperationException("Document BO.UstawNabywceWedlugId(int) not found");
        ustawNabywce.Invoke(bo, new object[] { dto.KontrahentId });
        _log.LogInformation("Buyer set via UstawNabywceWedlugId({id})", dto.KontrahentId);

        // 4. Dates. Both fiscal dates are computed by the Domain aggregate
        //    (SalesDocument.ComputeFiscalDates) and passed in resolved: DataSprzedazy is the
        //    sale/VAT month (may be in the past); DataWydaniaWystawienia is the dispatch/entry
        //    date. The service no longer derives them inline.
        _acc.SetProperty(dane, daneType, "DataSprzedazy", dto.DataSprzedazy);
        _acc.SetProperty(dane, daneType, "DataWydaniaWystawienia", dto.DataWydania);

        // 5. Add positions. Catalogue product -> Dodaj(symbol); product NOT in Subiekt
        //    -> DodajUslugeJednorazowa (one-time line).
        var dodaj = boType.GetMethod("Dodaj", SferaObjectAccessor.Flags, new[] { typeof(string) })
            ?? throw new InvalidOperationException("Document BO.Dodaj(string) not found");
        var dodajUsluga = boType.GetMethod("DodajUslugeJednorazowa", SferaObjectAccessor.Flags, new[] { typeof(string), typeof(decimal) });

        // Lines arrive already discount-folded by the adapter (SferaInvoiceIssuer applies
        // Domain.SalesDocument.FoldDiscounts), so no negative-price positions remain and the
        // per-line gross prices are final. The defensive skip below guards that contract.
        var allLines = dto.Lines ?? Array.Empty<SferaInvoiceLineInput>();

        foreach (var line in allLines)
        {
            if (line.CenaBrutto < 0) continue;   // defensive: discounts are folded upstream

            bool exists;
            using (var ce = conn.CreateCommand())
            {
                ce.CommandText = "SELECT COUNT(*) FROM ModelDanychContainer.Asortymenty WHERE Symbol = @s AND IsInRecycleBin = 0";
                ce.Parameters.AddWithValue("@s", line.TowarSymbol);
                exists = (int)ce.ExecuteScalar() > 0;
            }

            object? poz;
            if (exists)
            {
                poz = dodaj.Invoke(bo, new object[] { line.TowarSymbol })
                    ?? throw new InvalidOperationException($"Could not add position for product '{line.TowarSymbol}'");
                _acc.SetProperty(poz, poz.GetType(), "Ilosc", line.Ilosc);
            }
            else
            {
                if (dodajUsluga == null)
                    throw new InvalidOperationException("DodajUslugeJednorazowa(string,decimal) not found");
                var nazwa = !string.IsNullOrWhiteSpace(line.Name) ? line.Name! : line.TowarSymbol;
                poz = dodajUsluga.Invoke(bo, new object[] { nazwa, line.Ilosc })
                    ?? throw new InvalidOperationException($"Could not add one-time service '{nazwa}'");
                _log.LogInformation("Product '{s}' not in Subiekt — added one-time service '{n}' x {q}", line.TowarSymbol, nazwa, line.Ilosc);
            }

            var pozType = poz.GetType();

            // Honor the VAT rate from the request.
            var vatId = LookupStawkaVatId(conn, line.StawkaVAT);
            if (vatId != null)
                _acc.SetProperty(poz, pozType, "StawkaVatId", vatId.Value);
            else
                _log.LogWarning("VAT rate '{r}' not found in StawkiVat — keeping product default", line.StawkaVAT);

            // Honor the (already discount-folded) gross unit price.
            if (line.CenaBrutto > 0)
            {
                var effCena = line.CenaBrutto;
                var cenaProp = pozType.GetProperty("Cena", SferaObjectAccessor.Flags);
                var cena = cenaProp?.GetValue(poz);
                if (cena != null)
                {
                    var cenaT = cena.GetType();
                    _acc.SetProperty(cena, cenaT, "BruttoPrzedRabatem", effCena);
                    _acc.SetProperty(cena, cenaT, "BruttoPoRabacie", effCena);
                    if (cenaProp!.CanWrite) cenaProp.SetValue(poz, cena); // set back in case Cena is a copy
                }
                _acc.SetProperty(poz, pozType, "CenaRecznieEdytowana", true);
            }

            _log.LogDebug("Added line {symbol} x {ilosc} @ {cena} brutto, VAT {vat}",
                line.TowarSymbol, line.Ilosc, line.CenaBrutto, line.StawkaVAT);
        }

        // 6. Recompute netto/VAT split from the manual gross prices.
        _acc.InvokeIfExists(bo, boType, "Przelicz");

        // 6b. Add the default payment (a faktura requires a FormaPlatnosci).
        _acc.InvokeIfExists(bo, boType, "DodajPlatnosciDomyslne");
        _acc.InvokeIfExists(bo, boType, "DodajDomyslnaPlatnoscNatychmiastowaNaKwoteDokumentu");

        // 6c. Allow the invoice out even when stock is insufficient.
        _acc.InvokeIfExists(bo, boType, "IgnorujBlokadeRealizacjiPozycji");

        _acc.InvokeIfExists(bo, boType, "AutoSymbol");
        _acc.InvokeIfExists(bo, boType, "NadajNumer");

        // 7. Validate explicitly — Waliduj() throws a descriptive Sfera exception.
        var waliduj = boType.GetMethod("Waliduj", SferaObjectAccessor.Flags, Type.EmptyTypes);
        if (waliduj != null)
        {
            try
            {
                waliduj.Invoke(bo, null);
                _log.LogInformation("Waliduj() passed");
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                _log.LogError("Waliduj() failed: {msg}", tie.InnerException.Message);
                throw new InvalidOperationException("Sfera validation failed: " + tie.InnerException.Message);
            }
        }

        // Diagnostics before save: dump State, MoznaZapisac, FK fields and warning
        // collections (kept at Debug; PII/internals never at Information).
        try
        {
            _log.LogDebug("State = {v}", boType.GetProperty("State", SferaObjectAccessor.Flags)?.GetValue(bo));
            _log.LogDebug("MoznaZapisac = {v}", boType.GetProperty("MoznaZapisac", SferaObjectAccessor.Flags)?.GetValue(bo));

            foreach (var f in new[] { "MagazynId", "MojaFirmaId", "MiejsceSprzedazyId", "MiejsceWprowadzeniaId",
                                      "NabywcaSprzedawcaId", "PodmiotId", "PlatnikId", "FormaPlatnosciId",
                                      "KonfiguracjaId", "StatusDokumentuId", "KategoriaDokumentuId",
                                      "Symbol", "NumerWewnetrzny_PelnaSygnatura", "WystawilaOsobaId" })
            {
                var pr = daneType.GetProperty(f, SferaObjectAccessor.Flags);
                if (pr != null) _log.LogDebug("Dane.{f} = {v}", f, pr.GetValue(dane) ?? "NULL");
            }

            var inv = boType.GetProperty("InvalidData", SferaObjectAccessor.Flags)?.GetValue(bo);
            if (inv is System.Collections.IEnumerable invEnum)
            {
                var fieldErrs = new List<string>();
                foreach (var ent in invEnum) if (ent != null) _acc.ExtractEntityErrors(ent, fieldErrs);
                if (fieldErrs.Count > 0) _log.LogWarning("Field errors: {e}", string.Join(" || ", fieldErrs.Distinct()));
                else _log.LogWarning("InvalidData present but no field-level messages exposed");
            }

            foreach (var p in boType.GetProperties(SferaObjectAccessor.Flags))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                var n = p.Name;
                if (!(n.Contains("strzez") || n.Contains("Ostrzez") || n.Contains("Komunikat") || n.Contains("Blad") || n.Contains("Invalid")))
                    continue;
                object? val = null;
                try { val = p.GetValue(bo); } catch { continue; }
                if (val is System.Collections.IEnumerable en and not string)
                {
                    var items = new List<string>();
                    foreach (var it in en) if (it != null) items.Add(it.ToString() ?? "");
                    if (items.Count > 0) _log.LogWarning("{prop} ({n}): {items}", n, items.Count, string.Join(" || ", items));
                }
                else if (val != null)
                {
                    _log.LogDebug("{prop} = {v}", n, val);
                }
            }
        }
        catch (Exception ex) { _log.LogWarning("pre-save diag failed: {e}", ex.Message); }

        // 8. Persist.
        var zapisz = boType.GetMethod("Zapisz", SferaObjectAccessor.Flags, Type.EmptyTypes)
            ?? throw new InvalidOperationException("Document BO.Zapisz() not found");
        var saveResult = zapisz.Invoke(bo, null);
        var saved = saveResult is bool b && b;
        _log.LogInformation("Zapisz() returned {result}", saveResult);
        if (!saved)
            throw new InvalidOperationException($"Sfera rejected save (Zapisz=false). {_acc.CollectValidationErrors(bo, boType, includeDocumentLevel: true, includeStateHint: true)}");

        // 8b. Read back Id + full document number (NumerWewnetrzny_PelnaSygnatura).
        var id = _acc.GetProperty(dane, daneType, "Id") is int idv ? idv : -1;
        var numer = _acc.GetProperty(dane, daneType, "Symbol") as string ?? "";

        if (id > 0)
        {
            using var nc = conn.CreateCommand();
            nc.CommandText = "SELECT NumerWewnetrzny_PelnaSygnatura FROM ModelDanychContainer.Dokumenty WHERE Id = @id";
            nc.Parameters.AddWithValue("@id", id);
            if (nc.ExecuteScalar() is string s && !string.IsNullOrWhiteSpace(s)) numer = s;
        }

        _log.LogInformation("{factory} saved: id={id} numer={numer}", factoryMethod, id, numer);
        return (id, numer);
    }

    // Map an OL taxRate string to the StawkiVat.Id (Guid). Behaviour identical to legacy.
    private Guid? LookupStawkaVatId(Microsoft.Data.SqlClient.SqlConnection conn, string? taxRate)
    {
        if (string.IsNullOrWhiteSpace(taxRate)) return null;
        var sym = taxRate.Trim().ToLowerInvariant() switch
        {
            "np" or "np." or "nieopodatkowane" => "nieop.",
            "zwolnione" or "zw." => "zw",
            var s => s
        };
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT TOP 1 Id FROM ModelDanychContainer.StawkiVat WHERE Symbol = @s";
            cmd.Parameters.AddWithValue("@s", sym);
            if (cmd.ExecuteScalar() is Guid g) return g;
        }
        catch (Exception ex) { _log.LogWarning("LookupStawkaVatId('{r}') failed: {e}", taxRate, ex.Message); }
        return null;
    }
}
