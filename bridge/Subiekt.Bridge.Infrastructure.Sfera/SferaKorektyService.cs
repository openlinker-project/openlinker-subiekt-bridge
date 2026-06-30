using System.Linq.Expressions;
using Microsoft.Extensions.Logging;

namespace Subiekt.Bridge.Infrastructure.Sfera;

/// <summary>
/// Moved verbatim (move-and-wrap) from the legacy
/// <c>SferaApi.Services.KorektyService</c>. Business logic unchanged; runs
/// synchronously against a passed-in <see cref="SferaSession"/> (no Task.Run/lock —
/// <see cref="SferaWriteQueue"/> serializes), uses the shared
/// <see cref="SferaObjectAccessor"/> instead of its private InvokeIfExists, and takes
/// <see cref="SferaCorrectionInput"/> instead of the Api DTO.
/// </summary>
public sealed class SferaKorektyService
{
    private readonly ILogger<SferaKorektyService> _log;
    private readonly SferaObjectAccessor _acc;

    public SferaKorektyService(ILogger<SferaKorektyService> log)
    {
        _log = log;
        _acc = new SferaObjectAccessor(log);
    }

    public (int Id, string Numer) UtworzKorekte(SferaSession sfera, int origId, SferaCorrectionInput dto)
    {
        var uchwyt = sfera.Uchwyt;
        var conn = uchwyt.PodajPolaczenie();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();

        // Validate the source exists + detect type (PA vs FS) from its number prefix.
        string origNumer;
        using (var ck = conn.CreateCommand())
        {
            ck.CommandText = "SELECT NumerWewnetrzny_PelnaSygnatura FROM ModelDanychContainer.Dokumenty WHERE Id=@id";
            ck.Parameters.AddWithValue("@id", origId);
            origNumer = ck.ExecuteScalar() as string ?? throw new ArgumentException($"Dokument {origId} nie istnieje");
        }
        var isParagon = origNumer.TrimStart().StartsWith("PA", StringComparison.OrdinalIgnoreCase);

        // 1. Load the FULL original sales document via the shared IDokumenty.Znajdz loader.
        var origDane = uchwyt.LoadDokumentSprzedazyDane(origId)
            ?? throw new InvalidOperationException($"Nie udało się wczytać dokumentu {origId}");

        // 2. Create the korekta from the original.
        var korektyType = SferaReflection.RequireType("InsERT.Moria.Dokumenty.Logistyka.IKorektyDokumentowSprzedazy, InsERT.Moria.Dokumenty.Logistyka");
        var korekty = uchwyt.PodajObiektTypu(korektyType);
        var korMethodName = isParagon ? "UtworzZwrotDoParagonu" : "UtworzKorekteFakturySprzedazy";
        var utworzKor = korekty.GetType().GetMethods(SferaObjectAccessor.Flags)
            .FirstOrDefault(m => m.Name == korMethodName && m.GetParameters().Length == 1)
            ?? throw new InvalidOperationException($"{korMethodName}(DokumentDS) not found");
        var bo = utworzKor.Invoke(korekty, new[] { origDane })
            ?? throw new InvalidOperationException("UtworzKorekteFakturySprzedazy zwróciło null");
        var boType = bo.GetType();

        // Bypass soft blocks (same as the invoice path).
        foreach (var f in new[] { "IgnorujBlokade", "WylaczBlokowanieStanowPrzezRezerwacjeIlosciowa", "NieSprawdzajRealizacji" })
        {
            var fp = boType.GetProperty(f, SferaObjectAccessor.Flags);
            if (fp?.CanWrite == true) { try { fp.SetValue(bo, true); } catch { } }
        }
        var ignBlok = boType.GetMethod("IgnorujBlokadeRealizacjiPozycji", SferaObjectAccessor.Flags, Type.EmptyTypes);
        try { ignBlok?.Invoke(bo, null); } catch { }

        // 3. Correct positions to their new quantity and/or new GROSS unit price.
        //    KorygujPozycjeWedlugLp returns the PozycjaKorekty for that Lp; the new quantity
        //    goes through the 2-arg overload, and the new gross price is set on the returned
        //    correction line's Cena value-object (verified against a live Sfera install — there
        //    is no dedicated price-correction method). A single Przelicz/Zapisz follows below.
        var korygujLp = boType.GetMethod("KorygujPozycjeWedlugLp", SferaObjectAccessor.Flags, new[] { typeof(int) })
            ?? throw new InvalidOperationException("KorygujPozycjeWedlugLp(int) not found");
        var korygujLpIlosc = boType.GetMethod("KorygujPozycjeWedlugLp", SferaObjectAccessor.Flags, new[] { typeof(int), typeof(decimal) });
        foreach (var line in dto.Lines ?? Array.Empty<SferaCorrectionLineInput>())
        {
            object? pozKorekty;
            if (line.NowaIlosc.HasValue && korygujLpIlosc != null)
            {
                pozKorekty = korygujLpIlosc.Invoke(bo, new object[] { line.Lp, line.NowaIlosc.Value });
                _log.LogInformation("Korekta: pozycja Lp={lp} → nowa ilość {q}", line.Lp, line.NowaIlosc.Value);
            }
            else
            {
                pozKorekty = korygujLp.Invoke(bo, new object[] { line.Lp });
                if (line.NowaIlosc.HasValue)
                {
                    SetDecimalProp(pozKorekty, "Ilosc", line.NowaIlosc.Value);
                    _log.LogInformation("Korekta: pozycja Lp={lp} → nowa ilość {q} (Ilosc)", line.Lp, line.NowaIlosc.Value);
                }
            }

            if (line.NowaCena.HasValue)
            {
                ApplyPriceCorrection(pozKorekty, line.Lp, line.NowaCena.Value);
                _log.LogInformation("Korekta: pozycja Lp={lp} → nowa cena brutto {p}", line.Lp, line.NowaCena.Value);
            }
        }

        // 4. Reason for the correction (best-effort field name).
        if (!string.IsNullOrWhiteSpace(dto.Przyczyna))
        {
            var dane = boType.GetProperty("Dane", SferaObjectAccessor.Flags)?.GetValue(bo);
            foreach (var pn in new[] { "PrzyczynaKorekty", "Przyczyna", "PowodKorekty", "Uwagi" })
            {
                var pp = dane?.GetType().GetProperty(pn, SferaObjectAccessor.Flags);
                if (pp?.CanWrite == true && pp.PropertyType == typeof(string)) { try { pp.SetValue(dane, dto.Przyczyna); break; } catch { } }
            }
        }

        _acc.InvokeIfExists(bo, boType, "Przelicz");
        _acc.InvokeIfExists(bo, boType, "DodajPlatnosciDomyslne");
        _acc.InvokeIfExists(bo, boType, "AutoSymbol");
        _acc.InvokeIfExists(bo, boType, "NadajNumer");

        // 5. Persist.
        var zapisz = boType.GetMethod("Zapisz", SferaObjectAccessor.Flags, Type.EmptyTypes)
            ?? throw new InvalidOperationException("KorektaBO.Zapisz() not found");
        var saved = zapisz.Invoke(bo, null) is bool b && b;
        _log.LogInformation("Korekta Zapisz() => {r}", saved);
        if (!saved) throw new InvalidOperationException("Sfera rejected save korekty (Zapisz=false).");

        var daneFin = boType.GetProperty("Dane", SferaObjectAccessor.Flags)?.GetValue(bo);
        var id = daneFin?.GetType().GetProperty("Id", SferaObjectAccessor.Flags)?.GetValue(daneFin) is int idv ? idv : -1;
        string numer = "";
        if (id > 0)
        {
            using var nc = conn.CreateCommand();
            nc.CommandText = "SELECT NumerWewnetrzny_PelnaSygnatura FROM ModelDanychContainer.Dokumenty WHERE Id=@id";
            nc.Parameters.AddWithValue("@id", id);
            if (nc.ExecuteScalar() is string sv && !string.IsNullOrWhiteSpace(sv)) numer = sv;
        }
        _log.LogInformation("Korekta zapisana: id={id} numer={numer} (do dok {orig})", id, numer, origId);
        return (id, numer);
    }

    // Sets the corrected GROSS unit price on a PozycjaKorekty returned by KorygujPozycjeWedlugLp.
    // Verified against a live Sfera (InsERT nexo) install via the /api/diag/korekta-poz +
    // /api/diag/type-props probes: InsERT.Moria.ModelDanych.PozycjaKorekty exposes a writable
    // `Cena` value-object whose GROSS fields are BruttoPrzedRabatem / BruttoPoRabacie, plus a
    // `CenaRecznieEdytowana` flag that keeps the manual price through the recompute. There is NO
    // dedicated price-correction method on the BO. The subsequent Przelicz() recomputes net/VAT/
    // Wartosc and the before/after delta from the edited gross. FAILS LOUD if the expected
    // members are absent — a silent no-op would report a successful correction while changing
    // nothing (a fiscal hazard).
    private void ApplyPriceCorrection(object? pozKorekty, int lp, decimal nowaCenaBrutto)
    {
        if (pozKorekty == null)
            throw new InvalidOperationException($"KorygujPozycjeWedlugLp({lp}) returned null — cannot set corrected price.");
        var pt = pozKorekty.GetType();

        var cena = pt.GetProperty("Cena", SferaObjectAccessor.Flags)?.GetValue(pozKorekty)
            ?? throw new InvalidOperationException("PozycjaKorekty.Cena is null/absent — cannot set corrected gross price.");
        var ct = cena.GetType();

        var set = false;
        foreach (var grossField in new[] { "BruttoPrzedRabatem", "BruttoPoRabacie" })
        {
            var gp = ct.GetProperty(grossField, SferaObjectAccessor.Flags);
            if (gp?.CanWrite == true && gp.PropertyType == typeof(decimal))
            {
                gp.SetValue(cena, nowaCenaBrutto);
                set = true;
            }
        }
        if (!set)
            throw new InvalidOperationException(
                "Brak zapisywalnego pola ceny brutto (BruttoPrzedRabatem/BruttoPoRabacie) na PozycjaKorekty.Cena — zweryfikuj przez /api/diag/korekta-bo.");

        // Mark the price as manually edited so the recompute keeps it (does not re-price from the cennik).
        var flag = pt.GetProperty("CenaRecznieEdytowana", SferaObjectAccessor.Flags);
        if (flag?.CanWrite == true && flag.PropertyType == typeof(bool))
        {
            try { flag.SetValue(pozKorekty, true); } catch { /* best-effort flag */ }
        }
    }

    private static void SetDecimalProp(object? target, string name, decimal value)
    {
        if (target == null) return;
        var p = target.GetType().GetProperty(name, SferaObjectAccessor.Flags);
        if (p?.CanWrite == true && p.PropertyType == typeof(decimal)) p.SetValue(target, value);
    }
}
