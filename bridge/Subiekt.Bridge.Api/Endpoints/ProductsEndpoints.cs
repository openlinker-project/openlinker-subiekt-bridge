using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SferaApi.Contracts;
using SferaApi.Models;
using SferaApi.Validation;
using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Infrastructure.Sfera;

namespace SferaApi.Endpoints;

// /api/products — product catalogue. Reads go through the 3A IProductCatalogReader
// (separate, non-Sfera-locked SQL connection — replaces the old inline SQL under
// the Sfera write lock). The create still runs through SferaWriteQueue.
public static class ProductsEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 1000;

    public static void MapProductsEndpoints(this IEndpointRouteBuilder app)
    {
        // LIST — enveloped (matches legacy: Data = { limit, count, items }).
        app.MapGet("/api/products", async (IProductCatalogReader reader, int limit) =>
        {
            if (ValidationGate.TryValidateLimit(limit, DefaultLimit, MaxLimit, out var effective, out var fail))
                return fail;

            var result = await reader.ListAsync(effective);
            if (result.IsFailure) return EndpointHelpers.ReadFailure(result.Error);

            var items = result.Value.Select(p => ProductItemResponse.From(p, includeRodzaj: true)).ToList();
            return EndpointHelpers.Ok(new { limit = effective, count = items.Count, items });
        });

        // GET by symbol — RAW shape (legacy returned { found, item } unenveloped).
        app.MapGet("/api/products/{symbol}", async (IProductCatalogReader reader, string symbol) =>
        {
            var result = await reader.GetBySymbolAsync(symbol);
            if (result.IsFailure) return EndpointHelpers.ReadFailure(result.Error);

            var view = result.Value;
            if (view is null) return Results.Ok(new { found = false });
            return Results.Ok(new { found = true, item = ProductItemResponse.From(view, includeRodzaj: false) });
        });

        // CREATE — enveloped. Runs serialized on the write queue via the moved
        // SferaAsortymentyService (preserves the legacy DTO's Opis).
        app.MapPost("/api/products", async (
            SferaWriteQueue writeQueue,
            SferaAsortymentyService asortymentyService,
            IAuditLog auditLog,
            CreateTowarRequestDto req) =>
        {
            if (req.TryValidate(ProductValidators.Create, out var validationFail))
                return validationFail;

            try
            {
                var inputJson = System.Text.Json.JsonSerializer.Serialize(req);
                var towarInput = new SferaProductInput(
                    Symbol: req.Symbol, Nazwa: req.Nazwa, Opis: req.Opis,
                    CenaEwidencyjna: req.CenaEwidencyjna, WzorzecSymbol: req.WzorzecSymbol);
                var (id, symbol, created) = await writeQueue.EnqueueAsync(
                    session => asortymentyService.UpsertTowar(session, towarInput));
                await auditLog.LogAsync("UpsertTowar", inputJson,
                    System.Text.Json.JsonSerializer.Serialize(new { id, symbol, created }), 200);
                return Results.Ok(new ResponseEnvelope<object>
                {
                    Success = true,
                    Data = new
                    {
                        providerProductId = id,
                        symbol,
                        nazwa = req.Nazwa,
                        created,
                        message = created ? "utworzono w Subiekcie" : "już istniał (zwrócono istniejący)"
                    }
                });
            }
            catch (Exception ex)
            {
                var bex = BridgeException.Classify(ex);
                await auditLog.LogAsync("UpsertTowar", System.Text.Json.JsonSerializer.Serialize(req), null, bex.HttpStatus, bex.Detail);
                return EndpointHelpers.BridgeFail(bex);
            }
        });
    }
}
