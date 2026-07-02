using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SferaApi.Models;
using Subiekt.Bridge.Application.Ports;

namespace SferaApi.Endpoints;

// /api/branches and /api/cash-registers — discovery endpoints (issue #5). Both are plain
// SQL reads (3A pattern, separate non-Sfera-locked connection), mirroring
// BankAccountsEndpoints. Responses use the unified ResponseEnvelope.
//
// IMPORTANT (scope revision, 2026-07-03): only `stanowiskoKasoweId` on POST /api/invoices
// is functional - selecting a branch (Oddzial) per invoice is NOT possible. Live
// investigation (docs/spikes/podmioty-oddzial-stanowisko-probe-findings.md s.8) proved a
// document's operative Oddzial comes from the LOGGED-IN SESSION's IKontekstBiznesowy (a
// read-only, session-bound business context), never from a per-document field - neither
// patching the document after creation nor passing Oddzial via ParametryTworzeniaDokumentu
// at creation time overrides it. GET /api/branches is kept as INFORMATIONAL ONLY (so an
// operator/OL admin can see which branches are configured); it does not feed into invoice
// issuance at all.
public static class BranchesEndpoints
{
    public static void MapBranchesEndpoints(this IEndpointRouteBuilder app)
    {
        // LIST — every Oddzial (branch). Informational only - see the class doc-comment.
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

        // LIST — every Stanowisko Kasowe. `id` is the value to pass as `stanowiskoKasoweId`
        // on POST /api/invoices (the only functional selector - see the class doc-comment).
        // `oddzialId` on each row and the optional `?oddzialId=` filter are informational
        // only (which branch a station happens to be linked to) - they do not affect
        // whether a station is usable, since branch routing itself is not supported.
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
