using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SferaApi.Models;
using Subiekt.Bridge.Infrastructure.Sfera;

namespace SferaApi.Endpoints;

// /health (anonymous — exempt from the /api auth middleware) plus the Sfera
// session connect/status endpoints. /health distinguishes "bridge down" from
// "Sfera session bad" from "Subiekt/SQL unreachable".
public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", (SferaSession s) =>
        {
            var sessionValid = s.IsConnected;
            var subiektReachable = s.TrySqlPing(out var pingError);

            var overall = sessionValid && subiektReachable ? "ok"
                : !sessionValid ? "sfera_session_invalid"
                : "subiekt_unreachable";

            return Results.Ok(new
            {
                status = overall,
                bridge = "up",
                sferaSession = sessionValid ? "valid" : "invalid",
                subiekt = subiektReachable ? "reachable" : "down",
                subiektError = pingError,
                time = DateTimeOffset.Now
            });
        });

        app.MapPost("/api/session/connect", (SferaSession s) =>
        {
            lock (s.SyncRoot) s.Connect();
            return Results.Ok(new ResponseEnvelope<object>
            {
                Success = true,
                Data = new { connected = true }
            });
        });

        app.MapGet("/api/session/status", (SferaSession s) =>
            Results.Ok(new ResponseEnvelope<object>
            {
                Success = true,
                Data = new { connected = s.IsConnected }
            }));
    }
}
