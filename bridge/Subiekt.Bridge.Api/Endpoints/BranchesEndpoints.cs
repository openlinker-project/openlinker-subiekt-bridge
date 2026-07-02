using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SferaApi.Models;
using Subiekt.Bridge.Application.Ports;

namespace SferaApi.Endpoints;

// /api/branches and /api/cash-registers — discovery endpoints for issue #5's Oddzial/
// Stanowisko Kasowe selector on POST /api/invoices. Both are plain SQL reads (3A pattern,
// separate non-Sfera-locked connection), mirroring BankAccountsEndpoints. Responses use the
// unified ResponseEnvelope.
public static class BranchesEndpoints
{
    public static void MapBranchesEndpoints(this IEndpointRouteBuilder app)
    {
        // LIST — every Oddzial (branch).
        app.MapGet("/api/branches", async (IBranchesReader reader) =>
        {
            var result = await reader.ListAsync();
            if (result.IsFailure) return EndpointHelpers.ReadFailure(result.Error);

            var branches = result.Value.Select(b => new
            {
                id = b.Id,
                name = b.Nazwa,
            }).ToList();

            return Results.Ok(new ResponseEnvelope<object>
            {
                Success = true,
                Data = new { count = branches.Count, branches }
            });
        });

        // LIST — every Stanowisko Kasowe, with its Oddzial link (null = unlinked, reserved
        // for the document's implicit-default branch - see CashRegisterView doc-comment).
        // Optional ?oddzialId= filters to stations VALID for that Oddzial: explicitly linked
        // to it (unlinked stations are excluded once an explicit Oddzial is requested - live
        // probing showed they are NOT usable from a non-default branch).
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
