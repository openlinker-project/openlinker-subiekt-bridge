using System.Reflection;
using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;

namespace Subiekt.Bridge.Infrastructure.Sfera;

/// <summary>
/// Warehouse goods receipt (przyjęcie magazynowe / przychód wewnętrzny — PW) through the
/// live Sfera API. Replaces the legacy raw-SQL MERGE into <c>StanyMagazynowe</c> (which
/// bypassed Subiekt's accounting — no document, no batches, corrupt valuation). Creates a
/// real PW document so stock is updated through Subiekt's own business logic.
/// <para>
/// PW (przychód wewnętrzny) is the right document for a supplier-less stock increase (the
/// external receipt PZ requires a vendor). Mirrors the proven create-and-save pattern of
/// <see cref="SferaDokumentySprzedazyService"/>: obtain the <c>IPrzychodyWewnetrzne</c>
/// facade off the live <see cref="SferaSession"/>, create the BO via <c>Utworz()</c>, add
/// the position via <c>Dodaj(symbol, qty)</c>, price + recompute, apply stock effects, then
/// <c>Zapisz()</c>. The target warehouse is selected the UoW-safe way: the session business
/// context is pointed at it (<c>Kontekst().UstawMagazynWedlugSymbolu</c>) BEFORE the document
/// is created, so the document's warehouse navigation is sponsored by its own UnitOfWork (a
/// raw MagazynId FK throws UnsponsoredModificationException). The context is restored to the
/// document's default warehouse afterwards. Facade/BO/property names were confirmed by
/// introspecting the live binaries (PrzychodWewnetrznyBO / DokumentPW / PozycjaDokumentu).
/// Runs synchronously on the single write worker via <see cref="SferaWriteQueue"/>.
/// </para>
/// </summary>
public sealed class SferaPrzyjeciaService
{
    private const BindingFlags Flags = SferaObjectAccessor.Flags;

    private readonly ILogger<SferaPrzyjeciaService> _log;
    private readonly SferaObjectAccessor _acc;

    public SferaPrzyjeciaService(ILogger<SferaPrzyjeciaService> log)
    {
        _log = log;
        _acc = new SferaObjectAccessor(log);
    }

    public (int Id, string Numer) Utworz(SferaSession sfera, SferaReceiptInput dto)
    {
        if (dto.Ilosc <= 0) throw new ArgumentException("ilosc must be > 0");

        var uchwyt = sfera.Uchwyt;
        var conn = uchwyt.PodajPolaczenie();
        if (conn.State != System.Data.ConnectionState.Open)
            conn.Open();

        // 1. Resolve product + warehouse (the receipt needs a real catalogue item and a
        //    real warehouse). Validating up-front gives a clean rejection message.
        using (var cmdM = conn.CreateCommand())
        {
            cmdM.CommandText = "SELECT COUNT(*) FROM ModelDanychContainer.Magazyny WHERE Symbol = @m";
            cmdM.Parameters.AddWithValue("@m", dto.Magazyn);
            if ((int)cmdM.ExecuteScalar() == 0)
                throw new ArgumentException($"Magazyn '{dto.Magazyn}' nie istnieje");
        }
        using (var cmdA = conn.CreateCommand())
        {
            cmdA.CommandText = "SELECT COUNT(*) FROM ModelDanychContainer.Asortymenty WHERE Symbol = @s AND IsInRecycleBin = 0";
            cmdA.Parameters.AddWithValue("@s", dto.Symbol);
            if ((int)cmdA.ExecuteScalar() == 0)
                throw new ArgumentException($"Towar '{dto.Symbol}' nie istnieje w Subiekcie");
        }

        // 2. Obtain the PW facade. Pick the parameterless Utworz() explicitly (the facade
        //    re-implements the interface, exposing two parameterless overloads → ambiguous).
        var ipwType = SferaReflection.RequireType("InsERT.Moria.Dokumenty.Logistyka.IPrzychodyWewnetrzne, InsERT.Moria.Dokumenty.Logistyka");
        var facade = uchwyt.PodajObiektTypu(ipwType);
        var factory = facade.GetType().GetMethods(Flags)
            .FirstOrDefault(m => m.Name == "Utworz" && m.GetParameters().Length == 0)
            ?? throw new InvalidOperationException("IPrzychodyWewnetrzne.Utworz() not found");
        object NewBo() => factory.Invoke(facade, null)
            ?? throw new InvalidOperationException("Utworz() returned null");

        // 3. Select the target warehouse UoW-safely. Create a document, read the default
        //    warehouse it carries; if it differs from the requested one, point the session
        //    context at the requested warehouse and recreate the document so its warehouse
        //    nav is sponsored by its own UnitOfWork. Restore the default context afterwards.
        var bo = NewBo();
        var defaultMag = ReadWarehouseSymbol(bo);

        // Same warehouse as the document default → no context mutation needed.
        if (string.Equals(defaultMag, dto.Magazyn, StringComparison.OrdinalIgnoreCase))
            return Build(bo, conn, dto);

        // We must switch warehouses. We can only do so safely if we know the default to
        // restore afterwards; if we couldn't read it, refuse rather than risk leaving the
        // shared session pointed at the wrong warehouse.
        if (defaultMag is null)
            throw new InvalidOperationException(
                $"Nie można ustalić domyślnego magazynu dokumentu — odmawiam przełączenia na '{dto.Magazyn}', " +
                "aby nie zostawić sesji ze złym magazynem kontekstu.");

        // Point the session context at the requested warehouse, recreate the document so its
        // warehouse nav is sponsored by its own UnitOfWork, build+save, then ALWAYS restore
        // the default context. The whole switched region is inside try/finally so any failure
        // (recreate, validation, save) still restores — a leaked context would silently
        // misroute every SUBSEQUENT write on this shared single-writer session.
        try
        {
            uchwyt.Kontekst().UstawMagazynWedlugSymbolu(dto.Magazyn);
            bo = NewBo();
            var nowMag = ReadWarehouseSymbol(bo);
            if (!string.Equals(nowMag, dto.Magazyn, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Nie udało się ustawić magazynu '{dto.Magazyn}' na dokumencie (dokument trafił do '{nowMag}').");
            return Build(bo, conn, dto);
        }
        finally
        {
            try { uchwyt.Kontekst().UstawMagazynWedlugSymbolu(defaultMag); }
            catch (Exception ex)
            {
                _log.LogError(ex, "KRYTYCZNE: nie udało się przywrócić domyślnego magazynu kontekstu '{m}'. " +
                    "Kolejne operacje zapisu mogą trafić do złego magazynu — zalecany restart procesu.", defaultMag);
            }
        }
    }

    private (int Id, string Numer) Build(object bo, Microsoft.Data.SqlClient.SqlConnection conn, SferaReceiptInput dto)
    {
        var boType = bo.GetType();
        var dane = boType.GetProperty("Dane", Flags)?.GetValue(bo)
            ?? throw new InvalidOperationException("PW BO.Dane is null");
        var daneType = dane.GetType();

        // Bypass soft blocks (best-effort; no-op when absent on PW).
        _acc.SetBoolFlag(bo, boType, "IgnorujBlokade", true);

        // Dates. Receipt date drives the stock movement.
        _acc.SetProperty(dane, daneType, "DataWprowadzenia", dto.DataPrzyjecia);
        _acc.SetProperty(dane, daneType, "DataWydaniaWystawienia", dto.DataPrzyjecia);
        if (!string.IsNullOrWhiteSpace(dto.Opis))
            _acc.SetProperty(dane, daneType, "Uwagi", dto.Opis);

        // Add the position by product symbol + quantity (inherits the document warehouse).
        var dodaj = boType.GetMethod("Dodaj", Flags, new[] { typeof(string), typeof(decimal) })
            ?? boType.GetMethod("Dodaj", Flags, new[] { typeof(string) })
            ?? throw new InvalidOperationException("PW BO.Dodaj(string[,decimal]) not found");
        var poz = (dodaj.GetParameters().Length == 2
                    ? dodaj.Invoke(bo, new object[] { dto.Symbol, dto.Ilosc })
                    : dodaj.Invoke(bo, new object[] { dto.Symbol }))
            ?? throw new InvalidOperationException($"Could not add receipt position for '{dto.Symbol}'");
        var pozType = poz.GetType();
        _acc.SetProperty(poz, pozType, "Ilosc", dto.Ilosc);

        if (!string.IsNullOrWhiteSpace(dto.NumerPartii))
            SetBatchOrThrow(poz, pozType, dto.NumerPartii!);

        // Price the receipt off the catalogue evidence price, then recompute + apply stock.
        _acc.InvokeIfExists(bo, boType, "UstawCeny");
        _acc.InvokeIfExists(bo, boType, "Przelicz");
        _acc.InvokeIfExists(bo, boType, "AplikujSkutkiMagazynowe");
        _acc.InvokeIfExists(bo, boType, "AutoSymbol");
        _acc.InvokeIfExists(bo, boType, "NadajNumer");

        // Validate (best-effort — PW may not expose a parameterless Waliduj()).
        var waliduj = boType.GetMethod("Waliduj", Flags, Type.EmptyTypes);
        if (waliduj != null)
        {
            try { waliduj.Invoke(bo, null); _log.LogInformation("PW Waliduj() passed"); }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                _log.LogError("PW Waliduj() failed: {msg}", tie.InnerException.Message);
                throw new InvalidOperationException("Sfera validation failed: " + tie.InnerException.Message);
            }
        }

        // Persist.
        var zapisz = boType.GetMethod("Zapisz", Flags, Type.EmptyTypes)
            ?? throw new InvalidOperationException("PW BO.Zapisz() not found");
        var saveResult = zapisz.Invoke(bo, null);
        var saved = saveResult is bool b && b;
        _log.LogInformation("PW Zapisz() returned {result}", saveResult);
        if (!saved)
            throw new InvalidOperationException($"Sfera rejected PW save (Zapisz=false). {_acc.CollectValidationErrors(bo, boType, includeDocumentLevel: true, includeStateHint: true)}");

        // Read back Id + full document number.
        var id = _acc.GetProperty(dane, daneType, "Id") is int idv ? idv : -1;
        var numer = _acc.GetProperty(dane, daneType, "Symbol") as string ?? "";
        if (id > 0)
        {
            using var nc = conn.CreateCommand();
            nc.CommandText = "SELECT NumerWewnetrzny_PelnaSygnatura FROM ModelDanychContainer.Dokumenty WHERE Id = @id";
            nc.Parameters.AddWithValue("@id", id);
            if (nc.ExecuteScalar() is string s && !string.IsNullOrWhiteSpace(s)) numer = s;
        }

        _log.LogInformation("PW saved: id={id} numer={numer} ({qty} x {sym} -> {mag})", id, numer, dto.Ilosc, dto.Symbol, dto.Magazyn);
        return (id, numer);
    }

    // Read the document's warehouse symbol from its (UoW-attached) Magazyn navigation.
    private string? ReadWarehouseSymbol(object bo)
    {
        var dane = bo.GetType().GetProperty("Dane", Flags)?.GetValue(bo);
        var magazyn = dane is null ? null : _acc.GetProperty(dane, dane.GetType(), "Magazyn");
        return magazyn is null ? null : _acc.GetProperty(magazyn, magazyn.GetType(), "Symbol") as string;
    }

    // Set the batch (partia) number on the receipt position. The position exposes its batch
    // through the PartiaInwentaryzacji nav (PartiaPozycji), whose NumerPartii is the human
    // batch number. If a batch number was supplied but the position can't carry one (e.g. the
    // product is not batch-tracked), we FAIL the receipt rather than silently drop the batch —
    // a saved receipt that lost its batch tag is silent stock/traceability corruption.
    private void SetBatchOrThrow(object poz, Type pozType, string numerPartii)
    {
        object? partia;
        try { partia = pozType.GetProperty("PartiaInwentaryzacji", Flags)?.GetValue(poz); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Nie udało się odczytać partii pozycji dla numeru '{numerPartii}': {ex.Message}", ex);
        }

        var partiaProp = partia?.GetType().GetProperty("NumerPartii", Flags);
        if (partia is null || partiaProp?.CanWrite != true)
            throw new InvalidOperationException(
                $"Podano numer partii '{numerPartii}', ale pozycja przyjęcia nie obsługuje partii w tym dokumencie " +
                "(towar prawdopodobnie nie jest partiowany). Przyjęcie odrzucone, aby nie zgubić informacji o partii.");

        _acc.SetProperty(partia, partia.GetType(), "NumerPartii", numerPartii);
        _log.LogInformation("PW batch set: {nr}", numerPartii);
    }
}
