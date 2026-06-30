using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SferaApi.Models;
using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Infrastructure.Sfera;

namespace SferaApi.Endpoints;

// /api/audit/last — the most recent audit-log entries (write-operation trail).
public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/audit/last", async (IAuditLog auditLog, int limit) =>
        {
            if (limit <= 0) limit = 10;
            var result = await auditLog.GetLastAsync(limit);
            if (result.IsFailure)
                return EndpointHelpers.BridgeFail(
                    BridgeException.Classify(new InvalidOperationException(result.Error.Message)));

            var logs = result.Value;
            return Results.Ok(new ResponseEnvelope<object>
            {
                Success = true,
                Data = new { count = logs.Count, items = logs }
            });
        });
    }
}
