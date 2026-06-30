using System.Text.Json.Serialization;
using Subiekt.Bridge.Application.Ports;

namespace SferaApi.Contracts;

// Stable response contract for the product (towar) read endpoints. The JSON key
// names are pinned with [JsonPropertyName] to EXACTLY match what the legacy
// inline-SQL Dictionary rows produced (the keys were the raw SQL column aliases:
// Id, Symbol, Nazwa, Opis, CenaEwidencyjna, PKWiU, KodCN, Numer, Rodzaj_Id), so
// the cockpit keeps deserializing the same shape after the switch to
// IProductCatalogReader.
public sealed record ProductItemResponse
{
    [JsonPropertyName("Id")] public int Id { get; init; }
    [JsonPropertyName("Symbol")] public string Symbol { get; init; } = "";
    [JsonPropertyName("Nazwa")] public string? Nazwa { get; init; }
    [JsonPropertyName("Opis")] public string? Opis { get; init; }
    [JsonPropertyName("CenaEwidencyjna")] public decimal? CenaEwidencyjna { get; init; }
    [JsonPropertyName("PKWiU")] public string? PKWiU { get; init; }
    [JsonPropertyName("KodCN")] public string? KodCN { get; init; }
    [JsonPropertyName("Numer")] public int? Numer { get; init; }

    // Present only on the list endpoint (the legacy {symbol} lookup did not select
    // Rodzaj_Id). Null is omitted by the contract for the single-item case.
    [JsonPropertyName("Rodzaj_Id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Rodzaj_Id { get; init; }

    // List rows carry Rodzaj_Id; detail rows do not (includeRodzaj=false).
    public static ProductItemResponse From(ProductView v, bool includeRodzaj) => new()
    {
        Id = v.Id,
        Symbol = v.Symbol,
        Nazwa = v.Nazwa,
        Opis = v.Opis,
        CenaEwidencyjna = v.CenaEwidencyjna,
        PKWiU = v.Pkwiu,
        KodCN = v.KodCn,
        Numer = v.Numer,
        Rodzaj_Id = includeRodzaj ? v.RodzajId : null
    };
}
