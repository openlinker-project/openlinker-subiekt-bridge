using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SferaApi.Contracts;
using SferaApi.Models;
using SferaApi.Validation;
using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Infrastructure.Sfera;   // BridgeException (error classification/mapping)

namespace SferaApi.Endpoints;

// /api/warehouses, /api/stock*, /api/batches* — warehouse reads go through the
// 3A IStockReader read-model (separate, non-Sfera-locked connection). /api/goods-receipts
// creates a real warehouse receipt (przyjęcie zewnętrzne / PZ) through the Sfera API
// via IWarehouseReceiver, so stock moves through Subiekt's own accounting (document +
// valuation + batches) instead of the removed raw-SQL MERGE into StanyMagazynowe.
public static class StockEndpoints
{
    private const int DefaultStockLimit = 200;
    private const int MaxLimit = 1000;

    public static void MapStockEndpoints(this IEndpointRouteBuilder app)
    {
        // RAW shape (legacy returned { count, items } unenveloped).
        app.MapGet("/api/warehouses", async (IStockReader reader) =>
        {
            var result = await reader.ListWarehousesAsync();
            if (result.IsFailure) return EndpointHelpers.ReadFailure(result.Error);
            var items = result.Value.Select(WarehouseItemResponse.From).ToList();
            return Results.Ok(new { count = items.Count, items });
        });

        // RAW shape (legacy returned { limit, count, items } unenveloped).
        app.MapGet("/api/stock", async (IStockReader reader, string? magazyn, string? symbol, int limit) =>
        {
            if (ValidationGate.TryValidateLimit(limit, DefaultStockLimit, MaxLimit, out var effective, out var fail))
                return fail;
            var result = await reader.ReadStockAsync(magazyn, symbol, effective);
            if (result.IsFailure) return EndpointHelpers.ReadFailure(result.Error);
            var items = result.Value.Select(StockLevelResponse.From).ToList();
            return Results.Ok(new { limit = effective, count = items.Count, items });
        });

        // RAW shape (legacy returned { towar, count, items } unenveloped).
        app.MapGet("/api/stock/{symbol}", async (IStockReader reader, string symbol) =>
        {
            var result = await reader.ReadStockBySymbolAsync(symbol);
            if (result.IsFailure) return EndpointHelpers.ReadFailure(result.Error);
            var items = result.Value.Select(StockBySymbolResponse.From).ToList();
            return Results.Ok(new { towar = symbol, count = items.Count, items });
        });

        // RAW shape (legacy returned { towar, count, items } unenveloped).
        // NOTE: the legacy route accepted an unused `magazyn` query param; kept in the
        // signature for binding compatibility though the read-model does not use it.
        app.MapGet("/api/batches/{symbol}", async (IStockReader reader, string symbol, string? magazyn) =>
        {
            var result = await reader.ReadBatchesAsync(symbol);
            if (result.IsFailure) return EndpointHelpers.ReadFailure(result.Error);
            var items = result.Value.Select(BatchResponse.From).ToList();
            return Results.Ok(new { towar = symbol, count = items.Count, items });
        });

        // ---------- Przyjęcie magazynowe (przychód wewnętrzny / PW) ----------
        // Creates a real PW document through Sfera (IWarehouseReceiver -> SferaWriteQueue),
        // so the stock movement goes through Subiekt's own accounting. The legacy raw-SQL
        // MERGE into StanyMagazynowe (which corrupted accounting) has been removed.
        app.MapPost("/api/goods-receipts", async (HttpContext ctx, IWarehouseReceiver receiver, IAuditLog auditLog) =>
        {
            string symbol; decimal ilosc; string magazyn; string? opis; string? numerPartii;
            try
            {
                using var reader = new System.IO.StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                var json = System.Text.Json.JsonDocument.Parse(body).RootElement;
                symbol = json.GetProperty("symbol").GetString() ?? "";
                ilosc = json.GetProperty("ilosc").GetDecimal();
                magazyn = json.TryGetProperty("magazyn", out var m) ? (m.GetString() ?? "MAG") : "MAG";
                opis = json.TryGetProperty("opis", out var o) ? o.GetString() : null;
                numerPartii = json.TryGetProperty("numer_partii", out var np) ? np.GetString() : null;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                return EndpointHelpers.ValidationFailure("Niepoprawne body — wymagane 'symbol' (string) i 'ilosc' (liczba).");
            }

            if (string.IsNullOrWhiteSpace(symbol)) return EndpointHelpers.ValidationFailure("symbol jest wymagany.");
            if (ilosc <= 0) return EndpointHelpers.ValidationFailure("ilosc musi być > 0.");

            var inputJson = System.Text.Json.JsonSerializer.Serialize(new { symbol, ilosc, magazyn, opis, numer_partii = numerPartii });
            var result = await receiver.ReceiveAsync(symbol, ilosc, magazyn, numerPartii, opis);
            if (result.IsFailure)
            {
                var bex = result.Error.Code == "unreachable"
                    ? BridgeException.Unreachable(result.Error.Message)
                    : BridgeException.Rejected(result.Error.Message);
                await auditLog.LogAsync("PrzyjecieMagazynowe", inputJson, null, bex.HttpStatus, result.Error.ToString());
                return EndpointHelpers.BridgeFail(bex);
            }

            var outputJson = System.Text.Json.JsonSerializer.Serialize(new { id = result.Value.Id, numer = result.Value.Numer });
            await auditLog.LogAsync("PrzyjecieMagazynowe", inputJson, outputJson, 200);
            return Results.Ok(new ResponseEnvelope<object>
            {
                Success = true,
                Data = new
                {
                    providerDocumentId = result.Value.Id,
                    providerDocumentNumber = result.Value.Numer,
                    state = "received",
                    symbol,
                    ilosc,
                    magazyn,
                    numer_partii = numerPartii
                }
            });
        });
    }
}
