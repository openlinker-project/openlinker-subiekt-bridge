using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Application.UseCases;

/// <summary>
/// Issue-invoice use case. Owns the idempotency policy ("a retried key returns the
/// same provider invoice, never a duplicate fiscal document; a fresh issue stores the
/// result; an issuer failure must not store; a store-READ that yields no value — miss
/// OR fault — proceeds to issue") beside the issue call so it is unit-testable. Pure
/// orchestration — no framework deps. Mirrors <see cref="UpsertCustomerHandler"/>.
/// </summary>
public sealed class IssueInvoiceHandler
{
    /// <summary>
    /// Stable error code stamped on a failure that occurred during the deferred document
    /// BUILD (mapper/validation) stage, as opposed to the issuer stage. The endpoint
    /// branches on this to choose 422 (build/validation) vs the issuer's contract status,
    /// instead of inferring the stage from a captured success side-effect.
    /// </summary>
    public const string BuildFailedCode = "build_failed";

    private readonly IIssueInvoiceWithBuyer _issuer;
    private readonly IIdempotencyStore _store;

    public IssueInvoiceHandler(IIssueInvoiceWithBuyer issuer, IIdempotencyStore store)
    {
        _issuer = issuer ?? throw new ArgumentNullException(nameof(issuer));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<Result<IssueInvoiceResult>> HandleAsync(
        IssueInvoiceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var hasKey = !string.IsNullOrWhiteSpace(command.IdempotencyKey);

        // Namespace the shared store under the invoice prefix (AC-I1) so an invoice key
        // can never collide with a PW key carrying the same raw string. Pre-existing bare
        // keys are migrated to this prefix at store Load() (AC-I4).
        var storeKey = hasKey ? IdempotencyKeyPrefixes.Fv + command.IdempotencyKey : null;

        // Idempotency gate FIRST (F4): a retried key returns the SAME provider invoice,
        // never a duplicate fiscal document. Only a real hit (success + non-null value)
        // short-circuits — a read FAULT or a miss both fall through to a fresh issue (F5).
        if (hasKey)
        {
            var hit = await _store.TryGetAsync(storeKey!, cancellationToken);
            if (hit.IsSuccess && hit.Value is { } prior)
            {
                return Result.Success(new IssueInvoiceResult(
                    prior.ProviderInvoiceId,
                    prior.ProviderInvoiceNumber,
                    WasIdempotentHit: true));
            }
        }

        // Non-hit: build the document NOW (deferred so the mapper never runs on a real
        // retry hit). A build failure propagates without issuing or storing (F4).
        // Attribute the STAGE in the returned error so the endpoint can pick the HTTP
        // status deterministically (build/validation => 422) instead of inferring it from
        // a side-effect. The mapper propagates arbitrary DOMAIN codes (buyer/vat/doc),
        // which are NOT a usable discriminator, so we re-wrap them under the stable
        // BuildFailedCode while keeping the original human-readable message verbatim.
        var built = command.BuildDocument();
        if (built.IsFailure)
            return Result.Failure<IssueInvoiceResult>(
                new Error(BuildFailedCode, built.Error.Message));

        var (document, buyer) = built.Value;

        var issued = await _issuer.IssueAsync(document, buyer, cancellationToken);
        if (issued.IsFailure)
            return Result.Failure<IssueInvoiceResult>(issued.Error);

        var id = issued.Value.Id;
        var numer = issued.Value.Numer;

        // Remember the result so a later retry hits the gate above. A store WRITE fault
        // is swallowed (Decision 5): the fiscal document already exists, so we still
        // return success rather than masking a real issue behind a cache problem.
        if (hasKey)
            await _store.StoreAsync(storeKey!, new IdempotentInvoice(id, numer), cancellationToken);

        return Result.Success(new IssueInvoiceResult(id, numer, WasIdempotentHit: false));
    }
}
