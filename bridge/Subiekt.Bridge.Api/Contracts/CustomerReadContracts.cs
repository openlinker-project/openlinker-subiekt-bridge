using System.Text.Json.Serialization;
using Subiekt.Bridge.Application.Ports;

namespace SferaApi.Contracts;

// Stable response contract for the customer (kontrahent) read endpoints. JSON keys
// are pinned to EXACTLY match the legacy inline-SQL Dictionary rows, whose keys
// were the SQL column aliases: Id, NazwaSkrocona, NIP, NIPSformatowany, Telefon,
// Sygnatura, Aktywny, Kontrahent. Switching to ICustomerQuery keeps the shape.
public sealed record CustomerItemResponse
{
    [JsonPropertyName("Id")] public int Id { get; init; }
    [JsonPropertyName("NazwaSkrocona")] public string? NazwaSkrocona { get; init; }
    [JsonPropertyName("NIP")] public string? NIP { get; init; }
    [JsonPropertyName("NIPSformatowany")] public string? NIPSformatowany { get; init; }
    [JsonPropertyName("Telefon")] public string? Telefon { get; init; }
    [JsonPropertyName("Sygnatura")] public string? Sygnatura { get; init; }
    [JsonPropertyName("Aktywny")] public bool? Aktywny { get; init; }
    [JsonPropertyName("Kontrahent")] public bool? Kontrahent { get; init; }

    public static CustomerItemResponse From(CustomerView v) => new()
    {
        Id = v.Id,
        NazwaSkrocona = v.NazwaSkrocona,
        NIP = v.Nip,
        NIPSformatowany = v.NipSformatowany,
        Telefon = v.Telefon,
        Sygnatura = v.Sygnatura,
        Aktywny = v.Aktywny,
        Kontrahent = v.Kontrahent
    };
}
