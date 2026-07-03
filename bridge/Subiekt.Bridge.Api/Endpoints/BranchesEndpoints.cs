using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SferaApi.Models;
using Subiekt.Bridge.Application.Ports;

namespace SferaApi.Endpoints;

// /api/branches — discovery endpoint (issue #5). Plain SQL read (3A pattern,
// separate non-Sfera-locked connection), mirroring BankAccountsEndpoints.
// Responses use the unified ResponseEnvelope.
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
    }
}
