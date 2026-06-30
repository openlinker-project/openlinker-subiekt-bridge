using Microsoft.Extensions.Logging;

namespace Subiekt.Bridge.Infrastructure.Sfera;

/// <summary>
/// Moved verbatim (move-and-wrap) from the legacy
/// <c>SferaApi.Services.AsortymentyService</c>. Business logic unchanged; runs
/// synchronously against a passed-in <see cref="SferaSession"/> (no Task.Run/lock —
/// <see cref="SferaWriteQueue"/> serializes), uses the shared
/// <see cref="SferaObjectAccessor"/> instead of its private SetEntity, and takes
/// <see cref="SferaProductInput"/> instead of the Api DTO.
/// </summary>
public sealed class SferaAsortymentyService
{
    private readonly ILogger<SferaAsortymentyService> _log;
    private readonly SferaObjectAccessor _acc;

    public SferaAsortymentyService(ILogger<SferaAsortymentyService> log)
    {
        _log = log;
        _acc = new SferaObjectAccessor(log);
    }

    public (int Id, string Symbol, bool Created) UpsertTowar(SferaSession sfera, SferaProductInput dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Symbol)) throw new ArgumentException("symbol is required");
        if (string.IsNullOrWhiteSpace(dto.Nazwa)) throw new ArgumentException("nazwa is required");

        var uchwyt = sfera.Uchwyt;
        var conn = uchwyt.PodajPolaczenie();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();

        // Idempotency: if the symbol already exists, return it (upsert).
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT TOP 1 Id FROM ModelDanychContainer.Asortymenty WHERE Symbol = @s AND IsInRecycleBin = 0";
            check.Parameters.AddWithValue("@s", dto.Symbol);
            if (check.ExecuteScalar() is int existingId)
            {
                _log.LogInformation("Towar '{s}' already exists id={id} — returning existing", dto.Symbol, existingId);
                return (existingId, dto.Symbol, false);
            }
        }

        // Pick a template product to copy VAT/unit/rodzaj from.
        var templateSymbol = dto.WzorzecSymbol;
        if (string.IsNullOrWhiteSpace(templateSymbol))
        {
            using var t = conn.CreateCommand();
            t.CommandText = @"
                SELECT TOP 1 a.Symbol
                FROM ModelDanychContainer.Asortymenty a
                LEFT JOIN ModelDanychContainer.StawkiVat sv ON sv.Id = a.StawkaVatSprzedaz_Id
                WHERE a.IsInRecycleBin = 0 AND a.StawkaVatSprzedaz_Id IS NOT NULL
                ORDER BY CASE WHEN sv.Symbol = '23' THEN 0 ELSE 1 END, a.Symbol";
            templateSymbol = t.ExecuteScalar() as string;
        }
        if (string.IsNullOrWhiteSpace(templateSymbol))
            throw new InvalidOperationException("Brak produktu-wzorca w kartotece (potrzebny do skopiowania VAT/jednostki).");

        var iType = SferaReflection.RequireType("InsERT.Moria.Asortymenty.IAsortymenty, InsERT.Moria.Asortymenty");
        var facade = uchwyt.PodajObiektTypu(iType);
        var facadeType = facade.GetType();

        // Load the template BO and its Dane (Asortyment entity).
        var znajdz = facadeType.GetMethod("Znajdz", SferaObjectAccessor.Flags, new[] { typeof(string) })
            ?? throw new InvalidOperationException("IAsortymenty.Znajdz(string) not found");
        var templateBo = znajdz.Invoke(facade, new object[] { templateSymbol })
            ?? throw new InvalidOperationException($"Wzorzec '{templateSymbol}' nie znaleziony");
        var templateDane = templateBo.GetType().GetProperty("Dane", SferaObjectAccessor.Flags)?.GetValue(templateBo)
            ?? throw new InvalidOperationException("Wzorzec.Dane is null");

        // Create the new product BO.
        var utworz = facadeType.GetMethod("Utworz", SferaObjectAccessor.Flags, Type.EmptyTypes)
            ?? throw new InvalidOperationException("IAsortymenty.Utworz() not found");
        var bo = utworz.Invoke(facade, null)
            ?? throw new InvalidOperationException("Utworz() returned null");
        var boType = bo.GetType();

        // Copy standard settings (VAT, unit, rodzaj...) from the template.
        var wypelnij = boType.GetMethod("WypelnijNaPodstawie", SferaObjectAccessor.Flags, new[] { templateDane.GetType() });
        if (wypelnij != null)
        {
            wypelnij.Invoke(bo, new[] { templateDane });
            _log.LogInformation("WypelnijNaPodstawie('{t}') — skopiowano VAT/jednostkę/rodzaj", templateSymbol);
        }
        else _log.LogWarning("WypelnijNaPodstawie not found — produkt może wymagać VAT/jednostki ręcznie");

        // Override identity + price with the Presta values.
        var dane = boType.GetProperty("Dane", SferaObjectAccessor.Flags)!.GetValue(bo)!;
        var daneType = dane.GetType();
        _acc.SetProperty(dane, daneType, "Symbol", dto.Symbol);
        _acc.SetProperty(dane, daneType, "Nazwa", dto.Nazwa);
        if (!string.IsNullOrWhiteSpace(dto.Opis)) _acc.SetProperty(dane, daneType, "Opis", dto.Opis);
        if (dto.CenaEwidencyjna > 0) _acc.SetProperty(dane, daneType, "CenaEwidencyjna", dto.CenaEwidencyjna);

        // Persist.
        var zapisz = boType.GetMethod("Zapisz", SferaObjectAccessor.Flags, Type.EmptyTypes)
            ?? throw new InvalidOperationException("AsortymentBO.Zapisz() not found");
        var saved = zapisz.Invoke(bo, null) is bool b && b;
        _log.LogInformation("Asortyment Zapisz() => {r}", saved);
        if (!saved)
            throw new InvalidOperationException($"Sfera rejected save (Zapisz=false) dla towaru '{dto.Symbol}'.");

        var id = daneType.GetProperty("Id", SferaObjectAccessor.Flags)?.GetValue(dane) is int idv ? idv : -1;
        // re-read by symbol if needed
        if (id <= 0)
        {
            using var rc = conn.CreateCommand();
            rc.CommandText = "SELECT Id FROM ModelDanychContainer.Asortymenty WHERE Symbol = @s AND IsInRecycleBin = 0";
            rc.Parameters.AddWithValue("@s", dto.Symbol);
            if (rc.ExecuteScalar() is int rid) id = rid;
        }

        _log.LogInformation("Towar utworzony: id={id} symbol={s} nazwa={n}", id, dto.Symbol, dto.Nazwa);
        return (id, dto.Symbol, true);
    }

    public bool Exists(SferaSession sfera, string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        var conn = sfera.Uchwyt.PodajPolaczenie();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ModelDanychContainer.Asortymenty WHERE Symbol = @s AND IsInRecycleBin = 0";
        cmd.Parameters.AddWithValue("@s", symbol);
        return (int)cmd.ExecuteScalar() > 0;
    }
}
