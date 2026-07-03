using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SferaApi.Models;
using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Infrastructure.Sfera;   // BridgeException (error classification/mapping)

namespace SferaApi.Endpoints;

// /api/bank-accounts — the seller's Subiekt bank accounts (issue #1).
// The LIST goes through the 3A IBankAccountsReader read-model (separate,
// non-Sfera-locked connection); the default-account FLIP goes through the
// IDefaultBankAccountWriter port (Podmiot business-object save on the Sfera
// write queue). Responses use the unified ResponseEnvelope.
public static class BankAccountsEndpoints
{
    public static void MapBankAccountsEndpoints(this IEndpointRouteBuilder app)
    {
        // LIST — active seller accounts, default first.
        app.MapGet("/api/bank-accounts", async (IBankAccountsReader reader) =>
        {
            var result = await reader.ListAsync();
            if (result.IsFailure) return EndpointHelpers.ReadFailure(result.Error);

            var accounts = result.Value.Select(a => new
            {
                id = a.Id,
                name = a.Nazwa,
                number = a.Numer,
                bankNumber = a.NumerBanku,
                description = a.Opis,
                currency = a.Waluta,
                isVatAccount = a.JestRachunkiemVAT,
                isDefault = a.IsDefault,
            }).ToList();

            return Results.Ok(new ResponseEnvelope<object>
            {
                Success = true,
                Data = new { count = accounts.Count, accounts }
            });
        });

        // SET DEFAULT — make {id} the seller's default ("Podstawowy") account.
        // Idempotent: selecting the current default succeeds without a write.
        app.MapPut("/api/bank-accounts/{id:int}/default", async (
            int id,
            IDefaultBankAccountWriter writer,
            IAuditLog auditLog) =>
        {
            // SetDefaultAsync never throws (it wraps everything, including
            // BridgeException.Classify, into the Result), so gating the audit log on
            // result.IsFailure below covers every outcome — there is no uncaught-exception
            // path that would skip logging.
            var result = await writer.SetDefaultAsync(id);
            if (result.IsFailure)
            {
                var bex = result.Error.Code == "unreachable"
                    ? BridgeException.Unreachable(result.Error.Message)
                    : BridgeException.Rejected(result.Error.Message);
                await auditLog.LogAsync("UstawRachunekPodstawowy", id.ToString(), null, bex.HttpStatus, result.Error.ToString());
                return EndpointHelpers.BridgeFail(bex);
            }

            await auditLog.LogAsync("UstawRachunekPodstawowy", id.ToString(),
                System.Text.Json.JsonSerializer.Serialize(new { bankAccountId = id, isDefault = true }), 200);
            return Results.Ok(new ResponseEnvelope<object>
            {
                Success = true,
                Data = new { bankAccountId = id, isDefault = true }
            });
        });
    }
}
