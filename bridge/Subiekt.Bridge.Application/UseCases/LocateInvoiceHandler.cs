using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Application.UseCases;

/// <summary>
/// The document a locate found, or the absence of one. Mirrors the OL-side
/// <c>BridgeLocateResponse</c>: <see cref="Found"/> false carries nothing else.
/// </summary>
public sealed record LocatedInvoice(
    int ProviderInvoiceId,
    string? Numer,
    string RegulatoryStatus,
    string? ClearanceReference);

/// <summary>
/// Crash-recovery locate: answer "did a document already get created under this
/// OL idempotency key?" after a process died mid-submit and OL no longer knows
/// whether its request landed.
///
/// <para>
/// Subiekt is a SELF-NUMBERING provider — the document number is assigned only
/// in the issue response — so the OL idempotency key is the only thing that
/// exists on BOTH sides before the crash, and therefore the only usable search
/// key. This handler reads it out of the very same <see cref="IIdempotencyStore"/>
/// entry <see cref="IssueInvoiceHandler"/> writes, which is what makes the two
/// halves impossible to drift: a key that would short-circuit a retried issue is
/// exactly a key this locate reports as found.
/// </para>
///
/// <para>
/// The nexo bridge deliberately does NOT reproduce the GT bridge's approach of
/// scanning a document column (<c>dok_NrPelnyOryg</c>) for the key. That column
/// is a GT schema detail with no Moria counterpart, and the store is a stronger
/// source anyway: it is written inside the issue use case rather than inferred
/// from where a string happened to be stamped.
/// </para>
/// </summary>
public sealed class LocateInvoiceHandler
{
    private readonly IIdempotencyStore _store;
    private readonly IDocumentStatusReader _statusReader;

    public LocateInvoiceHandler(IIdempotencyStore store, IDocumentStatusReader statusReader)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _statusReader = statusReader ?? throw new ArgumentNullException(nameof(statusReader));
    }

    /// <summary>
    /// Locate the document issued under <paramref name="idempotencyKey"/>.
    ///
    /// <para>
    /// Three outcomes, and the difference between them is the whole point:
    /// a successful <c>null</c> means "no document was created under this key",
    /// which the caller is entitled to act on by issuing; a FAILED result means
    /// "could not find out", which the caller must not read as an absence.
    /// </para>
    ///
    /// <para>
    /// This inverts <see cref="IssueInvoiceHandler"/>'s treatment of the same
    /// store read, deliberately. There, a read fault falls through to issuing,
    /// because the alternative is refusing to issue a document that may not
    /// exist. Here, reporting a fault as "not found" would tell OL it is safe to
    /// issue — and OL would then create the duplicate permanent fiscal document
    /// that this entire crash-recovery path exists to prevent. So the fault is
    /// propagated as <c>unreachable</c> and the caller retries.
    /// </para>
    /// </summary>
    public async Task<Result<LocatedInvoice?>> HandleAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return Result.Failure<LocatedInvoice?>(
                new Error("bad_request", "A locate requires a non-empty idempotency key."));

        // Same namespacing as the issue path (AC-I1), for the same reason: an
        // invoice key and a PW key carrying one raw string must not collide.
        var storeKey = IdempotencyKeyPrefixes.Fv + idempotencyKey;

        var hit = await _store.TryGetAsync(storeKey, cancellationToken);
        if (hit.IsFailure)
            return Result.Failure<LocatedInvoice?>(
                new Error("unreachable", $"Could not read the idempotency store: {hit.Error.Message}"));

        if (hit.Value is not { } prior)
            return Result.Success<LocatedInvoice?>(null);

        var status = await _statusReader.GetStatusAsync(prior.ProviderInvoiceId, cancellationToken);
        if (status.IsFailure)
            return Result.Failure<LocatedInvoice?>(status.Error);

        // A remembered id whose document no longer exists is a REAL absence, not a
        // fault: the reader's contract is that a genuine infrastructure problem is a
        // failed Result and `not_found` is only ever returned for a document that is
        // actually gone. Reporting it as found would hand OL a providerInvoiceId it
        // could never read back.
        if (string.Equals(status.Value.Status, "not_found", StringComparison.OrdinalIgnoreCase))
            return Result.Success<LocatedInvoice?>(null);

        return Result.Success<LocatedInvoice?>(new LocatedInvoice(
            prior.ProviderInvoiceId,
            // Prefer what the document says NOW over what was remembered at issue
            // time; the stored number is the fallback for a reader that omits it.
            status.Value.Numer ?? prior.ProviderInvoiceNumber,
            status.Value.Ksef?.Status ?? "none",
            status.Value.Ksef?.Reference));
    }
}
