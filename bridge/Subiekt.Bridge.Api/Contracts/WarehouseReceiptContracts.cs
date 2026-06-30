using System.Text.Json.Serialization;
using Subiekt.Bridge.Application.Ports;

namespace SferaApi.Contracts;

/// <summary>
/// Wire request for a warehouse receipt (przyjęcie magazynowe / PW). A single-line
/// receipt: <see cref="Ilosc"/> units of <see cref="Symbol"/> into warehouse
/// <see cref="Magazyn"/>, optionally onto batch <see cref="NumerPartii"/>.
/// <para>
/// <see cref="IdempotencyKey"/> (optional) makes a retried call return the SAME PW
/// instead of double-counting stock; it is capped at 128 chars by
/// <c>CreateWarehouseReceiptRequestValidator</c> (AC-V6) and prefixed with the
/// <see cref="IdempotencyKeyPrefixes.Pw"/> namespace before reaching the shared store.
/// </para>
/// </summary>
public sealed class CreateWarehouseReceiptRequestDto
{
    public string Symbol { get; set; } = "";
    public decimal Ilosc { get; set; }
    public string Magazyn { get; set; } = "";
    public string? NumerPartii { get; set; }

    // OL-contract correlation/idempotency field (additive; optional).
    public string? IdempotencyKey { get; set; }
}

/// <summary>
/// Stable response shape for a created PW. Mirrors the document-issue envelopes
/// (providerInvoiceId/providerInvoiceNumber are the generic provider-document fields the
/// OL contract uses for any created document; <c>idempotent</c> flags a retry hit).
/// </summary>
public sealed record WarehouseReceiptResponse
{
    [JsonPropertyName("providerInvoiceId")] public int ProviderDocumentId { get; init; }
    [JsonPropertyName("providerInvoiceNumber")] public string? ProviderDocumentNumber { get; init; }
    [JsonPropertyName("symbol")] public string Symbol { get; init; } = "";
    [JsonPropertyName("ilosc")] public decimal Ilosc { get; init; }
    [JsonPropertyName("magazyn")] public string Magazyn { get; init; } = "";
    [JsonPropertyName("numer_partii")] public string? NumerPartii { get; init; }
    [JsonPropertyName("state")] public string State { get; init; } = "received";

    public static WarehouseReceiptResponse From(DocumentRef doc, CreateWarehouseReceiptRequestDto req) => new()
    {
        ProviderDocumentId = doc.Id,
        ProviderDocumentNumber = doc.Numer,
        Symbol = req.Symbol,
        Ilosc = req.Ilosc,
        Magazyn = req.Magazyn,
        NumerPartii = req.NumerPartii
    };
}
