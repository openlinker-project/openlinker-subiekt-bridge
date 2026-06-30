using Microsoft.AspNetCore.Http;
using SferaApi.Models;
using Subiekt.Bridge.Domain.Common;
using Subiekt.Bridge.Infrastructure.Sfera;

namespace SferaApi.Endpoints;

// Shared response helpers for the endpoint modules. Centralises the unified
// ResponseEnvelope + BridgeException mapping the inline endpoints used in Program.cs
// so every module returns the same wire shape.
internal static class EndpointHelpers
{
    // Structured failure matching the OL contract: {success:false, error:{code,reason}}.
    // unreachable -> 503 (retryable infra), rejected -> 422 (business no).
    public static IResult BridgeFail(BridgeException ex) =>
        Results.Json(new ResponseEnvelope<object>
        {
            Success = false,
            Error = new BridgeError { Code = ex.CodeString, Reason = ex.Reason }
        }, statusCode: ex.HttpStatus);

    // Map a failed read-model Result onto the unified envelope. The SQL read-models
    // tag failures by CODE ("unreachable" = transient infra -> 503; otherwise the
    // generic rejected -> 422) and carry only safe, author-controlled messages, so
    // the message is surfaced verbatim (same as the legacy status endpoint did).
    public static IResult ReadFailure(Error error)
    {
        var bex = error.Code == "unreachable"
            ? BridgeException.Unreachable(error.Message)
            : BridgeException.Rejected(error.Message);
        return BridgeFail(bex);
    }

    // A 400 validation envelope (used by the FluentValidation gate in the modules).
    public static IResult ValidationFailure(string reason) =>
        Results.Json(new ResponseEnvelope<object>
        {
            Success = false,
            Error = new BridgeError { Code = "bad_request", Reason = reason }
        }, statusCode: StatusCodes.Status400BadRequest);

    // Success envelope shorthand.
    public static IResult Ok(object data) =>
        Results.Ok(new ResponseEnvelope<object> { Success = true, Data = data });
}
