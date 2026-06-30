using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SferaApi.Models;
using Subiekt.Bridge.Infrastructure.Sfera;

namespace SferaApi.Endpoints;

// /api/diag/* — introspection endpoints that dump BO shapes / DB rows. The caller
// (Program.cs) maps this module ONLY when Diagnostics:Enabled AND the host is in
// Development; they still sit behind the /api auth middleware. Leaking handlers
// (ex.ToString()) are replaced with generic 500s via DiagFail (rethrow -> global
// exception handler logs the detail under a correlationId).
public static class DiagnosticsEndpoints
{
    public static void MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        // ---------- Diagnostics: Asortyment type/discriminator ----------
        app.MapGet("/api/diag/asortyment-typ", (SferaSession s) =>
        {
            lock (s.SyncRoot)
            {
                if (!s.IsConnected) return DiagNotConnected();
                var conn = s.Uchwyt.PodajPolaczenie();
                if (conn.State != System.Data.ConnectionState.Open) conn.Open();

                var typeCols = new List<string>();
                using (var c = conn.CreateCommand())
                {
                    c.CommandText = @"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
                        WHERE TABLE_SCHEMA='ModelDanychContainer' AND TABLE_NAME='Asortymenty'
                          AND (COLUMN_NAME LIKE '%Rodzaj%' OR COLUMN_NAME LIKE '%Typ%' OR COLUMN_NAME LIKE '%Discriminator%' OR COLUMN_NAME='Sprzedazy' OR COLUMN_NAME LIKE '%Sprzed%')
                        ORDER BY COLUMN_NAME";
                    using var r = c.ExecuteReader();
                    while (r.Read()) typeCols.Add(r.GetString(0));
                }

                var samples = new List<object>();
                if (typeCols.Count > 0)
                {
                    var cols = string.Join(",", typeCols.Select(x => "[" + x + "]"));
                    using var c = conn.CreateCommand();
                    c.CommandText = $"SELECT Symbol,{cols} FROM ModelDanychContainer.Asortymenty WHERE Symbol IN ('OPKSK','BANAW200')";
                    using var r = c.ExecuteReader();
                    while (r.Read())
                    {
                        var row = new Dictionary<string, object?>();
                        for (int i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                        samples.Add(row);
                    }
                }
                return Results.Ok(new { typeColumns = typeCols, samples });
            }
        });

        // ---------- Diagnostics: Cena value-object + VAT rates ----------
        app.MapGet("/api/diag/cena-vat", (SferaSession s) =>
        {
            lock (s.SyncRoot)
            {
                if (!s.IsConnected) return DiagNotConnected();
                try
                {
                    var uchwyt = s.Uchwyt;
                    var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

                    var iType = SferaReflection.RequireType("InsERT.Moria.Dokumenty.Logistyka.IDokumentySprzedazy, InsERT.Moria.Dokumenty.Logistyka");
                    var facade = uchwyt.PodajObiektTypu(iType);
                    var bo = iType.GetMethod("UtworzFaktureSprzedazy")!.Invoke(facade, null)!;
                    var boT = bo.GetType();

                    var conn = uchwyt.PodajPolaczenie();
                    if (conn.State != System.Data.ConnectionState.Open) conn.Open();
                    string? sym;
                    using (var c = conn.CreateCommand())
                    {
                        c.CommandText = "SELECT TOP 1 Symbol FROM ModelDanychContainer.Asortymenty WHERE IsInRecycleBin = 0 ORDER BY Symbol";
                        sym = c.ExecuteScalar() as string;
                    }
                    var poz = boT.GetMethod("Dodaj", flags, new[] { typeof(string) })!.Invoke(bo, new object[] { sym! })!;
                    var pozT = poz.GetType();

                    object? cenaInfo = null;
                    var cenaProp = pozT.GetProperty("Cena", flags);
                    if (cenaProp != null)
                    {
                        var cena = cenaProp.GetValue(poz);
                        var ct = cena?.GetType() ?? cenaProp.PropertyType;
                        cenaInfo = new
                        {
                            type = ct.FullName,
                            writableProps = ct.GetProperties(flags).Where(p => p.CanWrite).Select(p => new { p.Name, t = p.PropertyType.Name }).ToList(),
                            allProps = ct.GetProperties(flags).Select(p => new { p.Name, t = p.PropertyType.Name }).ToList(),
                            ctors = ct.GetConstructors().Select(c => string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))).ToList(),
                            currentValue = cena?.ToString()
                        };
                    }

                    var priceMethods = pozT.GetMethods(flags).Where(m => !m.IsSpecialName && (m.Name.Contains("Cen") || m.Name.Contains("Vat")))
                        .Select(m => $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})").Distinct().ToList();

                    var stawkaProp = pozT.GetProperty("StawkaVat", flags);
                    var stawkaType = stawkaProp?.PropertyType;
                    var stawkaProps = stawkaType?.GetProperties(flags).Select(p => new { p.Name, t = p.PropertyType.Name }).ToList();

                    var vatRows = new List<object>();
                    try
                    {
                        using var vc = conn.CreateCommand();
                        vc.CommandText = "SELECT TOP 30 * FROM ModelDanychContainer.StawkiVat";
                        using var rr = vc.ExecuteReader();
                        while (rr.Read())
                        {
                            var row = new Dictionary<string, object?>();
                            for (int i = 0; i < rr.FieldCount; i++) row[rr.GetName(i)] = rr.IsDBNull(i) ? null : rr.GetValue(i);
                            vatRows.Add(row);
                        }
                    }
                    catch (Exception vex) { vatRows.Add(new { error = vex.Message }); }

                    return Results.Ok(new
                    {
                        symbolUsed = sym,
                        cena = cenaInfo,
                        priceMethods,
                        stawkaVatType = stawkaType?.FullName,
                        stawkaVatProps = stawkaProps,
                        stawkiVatDb = vatRows
                    });
                }
                catch (Exception ex) { return DiagFail(ex); }
            }
        });

        // ---------- Diagnostics: introspect the invoice BO + Dokumenty columns ----------
        app.MapGet("/api/diag/faktura-bo", (SferaSession s) =>
        {
            lock (s.SyncRoot)
            {
                if (!s.IsConnected) return DiagNotConnected();
                try
                {
                    var uchwyt = s.Uchwyt;
                    var iType = SferaReflection.RequireType("InsERT.Moria.Dokumenty.Logistyka.IDokumentySprzedazy, InsERT.Moria.Dokumenty.Logistyka");
                    var facade = uchwyt.PodajObiektTypu(iType);
                    var bo = iType.GetMethod("UtworzFaktureSprzedazy")!.Invoke(facade, null)!;
                    var t = bo.GetType();
                    var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

                    var props = t.GetProperties(flags).Select(p => new
                    {
                        name = p.Name,
                        type = p.PropertyType.Name,
                        canWrite = p.CanWrite
                    }).OrderBy(p => p.name).ToList();

                    var methods = t.GetMethods(flags)
                        .Where(m => !m.IsSpecialName)
                        .Select(m => $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})")
                        .OrderBy(x => x)
                        .ToList();

                    var nested = new Dictionary<string, object?>();
                    foreach (var p in t.GetProperties(flags))
                    {
                        if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                        var n = p.Name;
                        if (n is "Dane" or "Data" || n.Contains("Pozycj") || n.Contains("Kontrahent") || n.Contains("Nabywca"))
                        {
                            object? val = null;
                            try { val = p.GetValue(bo); } catch { }
                            var childType = val?.GetType().Name ?? p.PropertyType.Name;
                            var writable = val?.GetType().GetProperties(flags).Where(c => c.CanWrite).Select(c => c.Name).OrderBy(x => x).ToList();
                            nested[n] = new { childType, writableProps = writable };
                        }
                    }

                    object? pozInfo = null;
                    try
                    {
                        var conn2 = uchwyt.PodajPolaczenie();
                        if (conn2.State != System.Data.ConnectionState.Open) conn2.Open();
                        string? symbol = null;
                        using (var c = conn2.CreateCommand())
                        {
                            c.CommandText = "SELECT TOP 1 Symbol FROM ModelDanychContainer.Asortymenty WHERE IsInRecycleBin = 0 ORDER BY Symbol";
                            symbol = c.ExecuteScalar() as string;
                        }
                        if (symbol != null)
                        {
                            var dodaj = t.GetMethod("Dodaj", flags, new[] { typeof(string) });
                            var poz = dodaj?.Invoke(bo, new object[] { symbol });
                            if (poz != null)
                            {
                                var pt = poz.GetType();
                                var pozWritable = pt.GetProperties(flags)
                                    .Where(p => p.CanWrite)
                                    .Select(p => new { name = p.Name, type = p.PropertyType.Name })
                                    .Where(p => p.name.Contains("Ilosc") || p.name.Contains("Cena") || p.name.Contains("Vat") || p.name.Contains("Stawka") || p.name.Contains("Rabat") || p.name.Contains("Wartosc"))
                                    .OrderBy(p => p.name).ToList();
                                pozInfo = new { symbolUsed = symbol, pozType = pt.FullName, relevantWritableProps = pozWritable };
                            }
                        }
                    }
                    catch (Exception pex) { pozInfo = new { error = pex.Message }; }

                    return Results.Ok(new
                    {
                        boType = t.FullName,
                        writableProps = props.Where(p => p.canWrite).ToList(),
                        methods,
                        nested,
                        pozycja = pozInfo
                    });
                }
                catch (Exception ex) { return DiagFail(ex); }
            }
        });

        app.MapGet("/api/diag/dokumenty-columns", (SferaSession s) =>
        {
            lock (s.SyncRoot)
            {
                if (!s.IsConnected) return DiagNotConnected();
                var conn = s.Uchwyt.PodajPolaczenie();
                if (conn.State != System.Data.ConnectionState.Open) conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT COLUMN_NAME, DATA_TYPE
                    FROM INFORMATION_SCHEMA.COLUMNS
                    WHERE TABLE_SCHEMA = 'ModelDanychContainer' AND TABLE_NAME = 'Dokumenty'
                    ORDER BY ORDINAL_POSITION";
                using var r = cmd.ExecuteReader();
                var cols = new List<object>();
                while (r.Read())
                    cols.Add(new { name = r.GetString(0), type = r.GetString(1) });
                return Results.Ok(new { count = cols.Count, columns = cols });
            }
        });

        // ---------- Diagnostics: introspect PodmiotBO returned by UtworzFirme ----------
        app.MapGet("/api/diag/podmiot-bo", (SferaSession s) =>
        {
            lock (s.SyncRoot)
            {
                if (!s.IsConnected) return DiagNotConnected();
                try
                {
                    var uchwyt = s.Uchwyt;
                    var iPodmiotyType = SferaReflection.RequireType("InsERT.Moria.Klienci.IPodmioty, InsERT.Moria.Klienci");
                    var iPodmioty = uchwyt.PodajObiektTypu(iPodmiotyType);
                    var utworz = iPodmiotyType.GetMethod("UtworzFirme");
                    var bo = utworz!.Invoke(iPodmioty, null)!;
                    var t = bo.GetType();

                    var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

                    var props = t.GetProperties(flags).Select(p => new
                    {
                        name = p.Name,
                        type = p.PropertyType.Name,
                        canWrite = p.CanWrite,
                        canRead = p.CanRead
                    }).OrderBy(p => p.name).ToList();

                    var methods = t.GetMethods(flags)
                        .Where(m => !m.IsSpecialName)
                        .Select(m => $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})")
                        .OrderBy(x => x)
                        .ToList();

                    var nested = new Dictionary<string, object?>();
                    foreach (var p in t.GetProperties(flags))
                    {
                        if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                        var pt = p.PropertyType;
                        if (pt.Name.Contains("Podmiot") || pt.Name.Contains("Dane") || pt.Name.Contains("Firma"))
                        {
                            object? childVal = null;
                            try { childVal = p.GetValue(bo); } catch { }
                            if (childVal != null)
                            {
                                var childProps = childVal.GetType().GetProperties(flags)
                                    .Where(cp => cp.CanWrite)
                                    .Select(cp => cp.Name)
                                    .OrderBy(x => x)
                                    .ToList();
                                nested[p.Name] = new { childType = childVal.GetType().Name, writableProps = childProps };
                            }
                            else
                            {
                                nested[p.Name] = new { childType = pt.Name, value = (string?)"null" };
                            }
                        }
                    }

                    return Results.Ok(new
                    {
                        boType = t.FullName,
                        boAssembly = t.Assembly.GetName().Name,
                        writableProps = props.Where(p => p.canWrite).ToList(),
                        readOnlyProps = props.Where(p => !p.canWrite).ToList(),
                        methods,
                        nestedEntities = nested
                    });
                }
                catch (Exception ex)
                {
                    return DiagFail(ex);
                }
            }
        });

        // ---------- Diagnostics: how to create a Korekta (correction) ----------
        app.MapGet("/api/diag/korekta-bo", (SferaSession s, int id) =>
        {
            lock (s.SyncRoot)
            {
                if (!s.IsConnected) return DiagNotConnected();
                try
                {
                    var uchwyt = s.Uchwyt;
                    var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
                    var iType = SferaReflection.RequireType("InsERT.Moria.Dokumenty.Logistyka.IKorektyDokumentowSprzedazy, InsERT.Moria.Dokumenty.Logistyka");
                    var facade = uchwyt.PodajObiektTypu(iType);
                    var facadeType = facade.GetType();

                    var facadeMethods = facadeType.GetMethods(flags).Where(m => !m.IsSpecialName)
                        .Select(m => $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})")
                        .OrderBy(x => x).Distinct().ToList();

                    object? bo = null; string? createErr = null;
                    try { bo = facadeType.GetMethod("UtworzKorekteFakturySprzedazy", flags, Type.EmptyTypes)?.Invoke(facade, null); }
                    catch (Exception ex) { createErr = ex.InnerException?.Message ?? ex.Message; }

                    object? boInfo = null;
                    if (bo != null)
                    {
                        var bt = bo.GetType();
                        var dane = bt.GetProperty("Dane", flags)?.GetValue(bo);
                        var daneWritable = dane?.GetType().GetProperties(flags).Where(p => p.CanWrite)
                            .Select(p => p.Name).Where(n => n.Contains("Przyczyn") || n.Contains("Data") || n.Contains("Numer") || n.Contains("Powod") || n.Contains("Korekt")).OrderBy(x => x).ToList();
                        // Widened to also surface price-correction methods (names containing
                        // "Cen") so the GROSS price-correction member is discoverable on a live box.
                        var boMethods = bt.GetMethods(flags).Where(m => !m.IsSpecialName && (m.Name.Contains("Wypelnij") || m.Name.Contains("Przyczyn") || m.Name.Contains("Koryguj") || m.Name.Contains("Cen") || m.Name == "Zapisz" || m.Name.Contains("Pozycj") || m.Name.Contains("UstawDokument") || m.Name.Contains("NaPodstawie")))
                            .Select(m => $"{m.ReturnType.Name} {m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})").Distinct().OrderBy(x => x).ToList();
                        var pozProp = bt.GetProperty("Pozycje", flags)?.GetValue(bo);
                        var pozMethods = pozProp?.GetType().GetMethods(flags).Where(m => !m.IsSpecialName).Select(m => $"{m.ReturnType.Name} {m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})").Distinct().OrderBy(x => x).Take(40).ToList();

                        // Dump a position object's writable "Cen*" properties (and any nested
                        // Cena value-object) so a GROSS price setter on the position is
                        // discoverable when no dedicated correction method exists.
                        object? pozPriceProps = null;
                        try
                        {
                            if (pozProp is System.Collections.IEnumerable pozItems)
                            {
                                foreach (var poz in pozItems)
                                {
                                    if (poz == null) continue;
                                    var pt = poz.GetType();
                                    var writableCen = pt.GetProperties(flags).Where(p => p.CanWrite && p.Name.Contains("Cen"))
                                        .Select(p => new { name = p.Name, type = p.PropertyType.Name }).OrderBy(p => p.name).ToList();
                                    var cenaVo = pt.GetProperty("Cena", flags)?.GetValue(poz);
                                    var cenaVoProps = cenaVo?.GetType().GetProperties(flags).Where(p => p.CanWrite)
                                        .Select(p => new { name = p.Name, type = p.PropertyType.Name }).OrderBy(p => p.name).ToList();
                                    pozPriceProps = new { pozType = pt.FullName, writableCenProps = writableCen, cenaVoType = cenaVo?.GetType().FullName, cenaVoWritableProps = cenaVoProps };
                                    break; // first position is representative of the shape
                                }
                            }
                        }
                        catch (Exception pex) { pozPriceProps = new { error = pex.Message }; }

                        boInfo = new { boType = bt.FullName, daneType = dane?.GetType().FullName, daneWritable, boMethods, pozycjeType = pozProp?.GetType().FullName, pozMethods, pozycjaPriceProps = pozPriceProps };
                    }

                    object? loadInfo = null;
                    try
                    {
                        var docsType = SferaReflection.RequireType("InsERT.Moria.Dokumenty.Logistyka.IDokumenty, InsERT.Moria.Dokumenty.Logistyka");
                        var docs = uchwyt.PodajObiektTypu(docsType);
                        var znajdzOverloads = docs.GetType().GetMethods(flags).Where(m => m.Name == "Znajdz")
                            .Select(m => $"Znajdz({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))}) -> {m.ReturnType.Name}").Distinct().ToList();
                        object? loaded = null; string? loadErr = null;
                        var byInt = docs.GetType().GetMethod("Znajdz", flags, new[] { typeof(int) });
                        if (byInt != null) { try { loaded = byInt.Invoke(docs, new object[] { id }); } catch (Exception ex) { loadErr = ex.InnerException?.Message ?? ex.Message; } }
                        var loadedDane = loaded?.GetType().GetProperty("Dane", flags)?.GetValue(loaded);
                        loadInfo = new { znajdzOverloads, hasZnajdzInt = byInt != null, loadedType = loaded?.GetType().FullName, loadedDaneType = loadedDane?.GetType().FullName, loadErr };
                    }
                    catch (Exception ex) { loadInfo = new { error = ex.Message }; }

                    return Results.Ok(new { facadeType = facadeType.FullName, createError = createErr, facadeMethods, korektaBo = boInfo, loadDocument = loadInfo });
                }
                catch (Exception ex) { return DiagFail(ex); }
            }
        });

        // ---------- Diagnostics: how to create an Asortyment (product) ----------
        app.MapGet("/api/diag/asortyment-facade", (SferaSession s) =>
        {
            lock (s.SyncRoot)
            {
                if (!s.IsConnected) return DiagNotConnected();
                try
                {
                    var uchwyt = s.Uchwyt;
                    var iType = SferaReflection.RequireType("InsERT.Moria.Asortymenty.IAsortymenty, InsERT.Moria.Asortymenty");
                    var facade = uchwyt.PodajObiektTypu(iType);
                    var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

                    var allMethods = facade.GetType().GetMethods(flags)
                        .Where(m => !m.IsSpecialName)
                        .Select(m => $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})")
                        .OrderBy(x => x).Distinct().ToList();

                    object? bo = null; string? usedFactory = null; object? danePropsErr = null;
                    foreach (var name in new[] { "UtworzTowar", "UtworzAsortyment", "Utworz", "UtworzNowy", "UtworzNowa" })
                    {
                        var mi = facade.GetType().GetMethod(name, flags, Type.EmptyTypes);
                        if (mi == null) continue;
                        try { bo = mi.Invoke(facade, null); usedFactory = name; break; }
                        catch (Exception ex) { danePropsErr = $"{name} threw: {ex.InnerException?.Message ?? ex.Message}"; }
                    }

                    object? daneInfo = null;
                    if (bo != null)
                    {
                        var boT = bo.GetType();
                        var dane = boT.GetProperty("Dane", flags)?.GetValue(bo);
                        var daneT = dane?.GetType();
                        var writable = daneT?.GetProperties(flags).Where(p => p.CanWrite)
                            .Select(p => $"{p.PropertyType.Name} {p.Name}")
                            .Where(n => n.Contains("Symbol") || n.Contains("Nazwa") || n.Contains("Cena") || n.Contains("Vat")
                                        || n.Contains("VAT") || n.Contains("Jednost") || n.Contains("Stawka") || n.Contains("Opis")
                                        || n.Contains("PKWiU") || n.Contains("Typ") || n.Contains("Rodzaj") || n.Contains("KodCN"))
                            .OrderBy(x => x).ToList();
                        var boMethods = boT.GetMethods(flags).Where(m => !m.IsSpecialName && (m.Name.Contains("Utworz") || m.Name == "Zapisz" || m.Name.Contains("Cena") || m.Name.Contains("Towar") || m.Name.Contains("Usluge")))
                            .Select(m => m.Name).Distinct().OrderBy(x => x).ToList();
                        daneInfo = new { boType = boT.FullName, daneType = daneT?.FullName, relevantWritable = writable, boMethods };
                    }

                    return Results.Ok(new { facadeType = facade.GetType().FullName, usedFactory, createError = danePropsErr, dane = daneInfo, allMethods });
                }
                catch (Exception ex) { return DiagFail(ex); }
            }
        });
    }

    // Diagnostics-only failure: rethrow so the global exception handler logs the
    // full detail under a correlationId and returns a generic 500 — never the raw
    // ex.ToString() (which leaked stack traces / internals before).
    private static IResult DiagFail(Exception ex) => throw ex;

    // Diagnostics-only "not connected" response in the unified envelope shape.
    private static IResult DiagNotConnected() =>
        Results.Json(new ResponseEnvelope<object>
        {
            Success = false,
            Error = new BridgeError { Code = "unreachable", Reason = "Sfera nie jest podłączona." }
        }, statusCode: 409);
}
