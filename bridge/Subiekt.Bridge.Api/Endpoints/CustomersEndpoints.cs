using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SferaApi.Contracts;
using SferaApi.Models;
using SferaApi.Validation;
using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Application.UseCases;
using Subiekt.Bridge.Infrastructure.Sfera;

namespace SferaApi.Endpoints;

// /api/customers — customers. Reads go through the 3A ICustomerQuery read-model
// (separate, non-locking SQL connection). The upsert runs through the hexagon
// (UpsertCustomerHandler -> ICustomerDirectory -> Sfera write queue); the wire
// contract is unchanged.
public static class CustomersEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 1000;

    public static void MapCustomersEndpoints(this IEndpointRouteBuilder app)
    {
        // LIST — enveloped (legacy: Data = { limit, count, items }).
        app.MapGet("/api/customers", async (ICustomerQuery query, int limit) =>
        {
            if (ValidationGate.TryValidateLimit(limit, DefaultLimit, MaxLimit, out var effective, out var fail))
                return fail;

            var result = await query.ListAsync(effective);
            if (result.IsFailure) return EndpointHelpers.ReadFailure(result.Error);

            var items = result.Value.Select(CustomerItemResponse.From).ToList();
            return EndpointHelpers.Ok(new { limit = effective, count = items.Count, items });
        });

        // GET by id — RAW shape (legacy returned { found, item } unenveloped).
        app.MapGet("/api/customers/{id:int}", async (ICustomerQuery query, int id) =>
        {
            var result = await query.GetByIdAsync(id);
            if (result.IsFailure) return EndpointHelpers.ReadFailure(result.Error);

            var view = result.Value;
            if (view is null) return Results.Ok(new { found = false });
            return Results.Ok(new { found = true, item = CustomerItemResponse.From(view) });
        });

        // UPSERT — enveloped. Runs through the Application handler (Domain validates
        // the NIP checksum + invariants); FluentValidation only shape-checks first.
        app.MapPost("/api/customers/upsert", async (
            UpsertCustomerHandler upsertCustomer,
            IAuditLog auditLog,
            CreateFirmaRequestDto req) =>
        {
            if (req.TryValidate(CustomerValidators.Upsert, out var validationFail))
                return validationFail;

            try
            {
                var inputJson = System.Text.Json.JsonSerializer.Serialize(req);

                var command = SferaApi.Contracts.UpsertCustomerContractMapper.FromLegacy(req);
                var result = await upsertCustomer.HandleAsync(command);

                if (result.IsFailure)
                {
                    // Distinguish PORT/INFRA errors from DOMAIN-VALIDATION errors by the
                    // Error.Code. The adapter tags infra/Sfera failures as exactly
                    // "unreachable"/"rejected": keep the no-leak guarantee (generic reason,
                    // full detail to the audit log). Any other code is a safe domain
                    // message — return 422 with the SPECIFIC reason.
                    var code = result.Error.Code;
                    if (code == "unreachable" || code == "rejected")
                    {
                        var bex = code == "unreachable"
                            ? BridgeException.Unreachable(result.Error.Message)
                            : BridgeException.Rejected(result.Error.Message);
                        await auditLog.LogAsync("UpsertFirma", inputJson, null, bex.HttpStatus, result.Error.ToString());
                        return EndpointHelpers.BridgeFail(bex);
                    }

                    await auditLog.LogAsync("UpsertFirma", inputJson, null, 422, result.Error.ToString());
                    return Results.Json(new ResponseEnvelope<object>
                    {
                        Success = false,
                        Error = new BridgeError { Code = "validation", Reason = result.Error.Message }
                    }, statusCode: 422);
                }

                var (id, numer) = (result.Value.Id, result.Value.Numer);
                var outputJson = System.Text.Json.JsonSerializer.Serialize(new { id, numer });
                await auditLog.LogAsync("UpsertFirma", inputJson, outputJson, 200);

                return Results.Ok(new ResponseEnvelope<object>
                {
                    Success = true,
                    Data = new { id, numer, nazwaSkrocona = req.NazwaSkrocona, nip = req.NIP }
                });
            }
            catch (Exception ex)
            {
                var bex = BridgeException.Classify(ex);
                await auditLog.LogAsync("UpsertFirma", System.Text.Json.JsonSerializer.Serialize(req), null, bex.HttpStatus, bex.Detail);
                return EndpointHelpers.BridgeFail(bex);
            }
        });
    }
}
