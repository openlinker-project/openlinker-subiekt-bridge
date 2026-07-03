using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SferaApi.Models;
using Subiekt.Bridge.Application.Ports;

namespace SferaApi.Endpoints;

// /api/cash-registers — discovery endpoint (issue #5). Plain SQL read (3A pattern,
// separate non-Sfera-locked connection), mirroring BankAccountsEndpoints.
// Responses use the unified ResponseEnvelope.
public static class CashRegistersEndpoints
{
    public static void MapCashRegistersEndpoints(this IEndpointRouteBuilder app)
    {
        // LIST — every Stanowisko Kasowe. `id` is the value to pass as `stanowiskoKasoweId`
        // on POST /api/invoices (the only functional selector - see BranchesEndpoints'
        // class doc-comment). `oddzialId` on each row and the optional `?oddzialId=` filter
        // are informational only (which branch a station happens to be linked to) - they
        // do not affect whether a station is usable, since branch routing itself is not
        // supported.
        app.MapGet("/api/cash-registers", async (ICashRegistersReader reader, int? oddzialId) =>
        {
            var result = await reader.ListAsync();
            if (result.IsFailure) return EndpointHelpers.ReadFailure(result.Error);

            var filtered = oddzialId is int wantedOddzial
                ? result.Value.Where(cr => cr.OddzialId == wantedOddzial)
                : result.Value;

            var registers = filtered.Select(cr => new
            {
                id = cr.Id,
                name = cr.Nazwa,
                symbol = cr.Symbol,
                oddzialId = cr.OddzialId,
            }).ToList();

            return Results.Ok(new ResponseEnvelope<object>
            {
                Success = true,
                Data = new { count = registers.Count, cashRegisters = registers }
            });
        });
    }
}
