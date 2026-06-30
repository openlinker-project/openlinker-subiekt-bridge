using FluentValidation;
using Microsoft.AspNetCore.Http;
using SferaApi.Endpoints;

namespace SferaApi.Validation;

// Manual, consistent FluentValidation gate used by the endpoint modules:
//   if (req.TryValidate(validator, out var fail)) return fail;
// On failure it produces a 400 ResponseEnvelope (bad_request) whose Reason joins
// the validation messages — keeping the unified envelope shape on every endpoint.
internal static class ValidationGate
{
    public static bool TryValidate<T>(this T instance, IValidator<T> validator, out IResult failure)
    {
        var result = validator.Validate(instance);
        if (result.IsValid)
        {
            failure = Results.Empty;
            return false;
        }

        var reason = string.Join(" ", result.Errors.Select(e => e.ErrorMessage));
        failure = EndpointHelpers.ValidationFailure(reason);
        return true;
    }

    // Clamp + validate a caller-supplied list limit. Non-positive falls back to the
    // default; anything above max is an explicit 400 (the read-models also clamp,
    // but a loud 400 surfaces an obviously bad request).
    public static bool TryValidateLimit(int limit, int fallback, int max, out int effective, out IResult failure)
    {
        if (limit > max)
        {
            effective = fallback;
            failure = EndpointHelpers.ValidationFailure($"limit nie może przekraczać {max}.");
            return true;
        }
        effective = limit <= 0 ? fallback : limit;
        failure = Results.Empty;
        return false;
    }
}
