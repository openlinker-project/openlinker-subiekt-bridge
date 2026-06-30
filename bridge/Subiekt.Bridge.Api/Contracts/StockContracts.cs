using System.Text.Json.Serialization;
using Subiekt.Bridge.Application.Ports;

namespace SferaApi.Contracts;

// Stable response contracts for the warehouse/stock read endpoints. JSON keys are
// pinned to match the legacy inline-SQL Dictionary rows.
//
// WIRE NOTES (see Faza-4 report — cockpit reconciliation):
//  * /api/stock list rows carry Asortyment_Id and Magazyn_Id; the read-model
//    exposes them (StockLevel.AsortymentId / MagazynId) and they are serialized
//    here under the legacy snake_case keys.
//  * /api/stock/{symbol} rows previously did NOT carry TowarSymbol/TowarNazwa;
//    the read-model returns them, but this per-symbol response OMITS them to keep
//    the exact legacy shape (the caller already knows the symbol). The per-symbol
//    rows also never carried the id columns, so this response omits them too.
//  * /api/batches/{symbol} rows carry Asortyment_Id (BatchView.AsortymentId),
//    serialized under the legacy snake_case key.

// /api/warehouses — keys: Id, Symbol, Nazwa, Opis.
public sealed record WarehouseItemResponse
{
    [JsonPropertyName("Id")] public int Id { get; init; }
    [JsonPropertyName("Symbol")] public string Symbol { get; init; } = "";
    [JsonPropertyName("Nazwa")] public string? Nazwa { get; init; }
    [JsonPropertyName("Opis")] public string? Opis { get; init; }

    public static WarehouseItemResponse From(WarehouseView v) => new()
    {
        Id = v.Id,
        Symbol = v.Symbol,
        Nazwa = v.Nazwa,
        Opis = v.Opis
    };
}

// /api/stock — keys: MagazynSymbol, MagazynNazwa, TowarSymbol, TowarNazwa, plus the
// four Ilosc* reservation figures.
public sealed record StockLevelResponse
{
    [JsonPropertyName("MagazynSymbol")] public string MagazynSymbol { get; init; } = "";
    [JsonPropertyName("MagazynNazwa")] public string? MagazynNazwa { get; init; }
    [JsonPropertyName("TowarSymbol")] public string? TowarSymbol { get; init; }
    [JsonPropertyName("TowarNazwa")] public string? TowarNazwa { get; init; }
    [JsonPropertyName("IloscDostepna")] public decimal IloscDostepna { get; init; }
    [JsonPropertyName("IloscZarezerwowanaIlosciowo")] public decimal IloscZarezerwowanaIlosciowo { get; init; }
    [JsonPropertyName("IloscZarezerwowanaDostawowo")] public decimal IloscZarezerwowanaDostawowo { get; init; }
    [JsonPropertyName("IloscZadysponowana")] public decimal IloscZadysponowana { get; init; }
    [JsonPropertyName("Asortyment_Id")] public int AsortymentId { get; init; }
    [JsonPropertyName("Magazyn_Id")] public int MagazynId { get; init; }

    public static StockLevelResponse From(StockLevel v) => new()
    {
        MagazynSymbol = v.MagazynSymbol,
        MagazynNazwa = v.MagazynNazwa,
        TowarSymbol = v.TowarSymbol,
        TowarNazwa = v.TowarNazwa,
        IloscDostepna = v.IloscDostepna,
        IloscZarezerwowanaIlosciowo = v.IloscZarezerwowanaIlosciowo,
        IloscZarezerwowanaDostawowo = v.IloscZarezerwowanaDostawowo,
        IloscZadysponowana = v.IloscZadysponowana,
        AsortymentId = v.AsortymentId,
        MagazynId = v.MagazynId
    };
}

// /api/stock/{symbol} — legacy rows carried ONLY the warehouse + Ilosc* figures
// (no TowarSymbol/TowarNazwa), so this projection omits them.
public sealed record StockBySymbolResponse
{
    [JsonPropertyName("MagazynSymbol")] public string MagazynSymbol { get; init; } = "";
    [JsonPropertyName("MagazynNazwa")] public string? MagazynNazwa { get; init; }
    [JsonPropertyName("IloscDostepna")] public decimal IloscDostepna { get; init; }
    [JsonPropertyName("IloscZarezerwowanaIlosciowo")] public decimal IloscZarezerwowanaIlosciowo { get; init; }
    [JsonPropertyName("IloscZarezerwowanaDostawowo")] public decimal IloscZarezerwowanaDostawowo { get; init; }
    [JsonPropertyName("IloscZadysponowana")] public decimal IloscZadysponowana { get; init; }

    public static StockBySymbolResponse From(StockLevel v) => new()
    {
        MagazynSymbol = v.MagazynSymbol,
        MagazynNazwa = v.MagazynNazwa,
        IloscDostepna = v.IloscDostepna,
        IloscZarezerwowanaIlosciowo = v.IloscZarezerwowanaIlosciowo,
        IloscZarezerwowanaDostawowo = v.IloscZarezerwowanaDostawowo,
        IloscZadysponowana = v.IloscZadysponowana
    };
}

// /api/batches/{symbol} — keys: PartiaId, NumerPartii, Ilosc, Termin, TowarSymbol,
// TowarNazwa.
public sealed record BatchResponse
{
    [JsonPropertyName("PartiaId")] public int PartiaId { get; init; }
    [JsonPropertyName("NumerPartii")] public string? NumerPartii { get; init; }
    [JsonPropertyName("Ilosc")] public decimal Ilosc { get; init; }
    [JsonPropertyName("Termin")] public DateTime? Termin { get; init; }
    [JsonPropertyName("TowarSymbol")] public string? TowarSymbol { get; init; }
    [JsonPropertyName("TowarNazwa")] public string? TowarNazwa { get; init; }
    [JsonPropertyName("Asortyment_Id")] public int AsortymentId { get; init; }

    public static BatchResponse From(BatchView v) => new()
    {
        PartiaId = v.PartiaId,
        NumerPartii = v.NumerPartii,
        Ilosc = v.Ilosc,
        Termin = v.Termin,
        TowarSymbol = v.TowarSymbol,
        TowarNazwa = v.TowarNazwa,
        AsortymentId = v.AsortymentId
    };
}
