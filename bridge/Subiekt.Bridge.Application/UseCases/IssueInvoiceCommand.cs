using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;
using Subiekt.Bridge.Domain.Invoices;

namespace Subiekt.Bridge.Application.UseCases;

/// <summary>
/// Command to issue a sales document idempotently. Carries the raw idempotency key
/// and a DEFERRED build delegate (not an already-built document): the handler checks
/// the idempotency gate FIRST and only invokes <see cref="BuildDocument"/> on a
/// non-hit, preserving today's gate-before-build ordering (the mapper never runs on a
/// real retry hit). Application stays ignorant of the HTTP request DTO — the endpoint
/// supplies the delegate as <c>() =&gt; InvoiceContractMapper.Build(req, currency)</c>.
/// </summary>
public sealed record IssueInvoiceCommand(
    string? IdempotencyKey,
    Func<Result<(SalesDocument Document, InlineBuyer? Buyer)>> BuildDocument);

/// <summary>
/// Result of <see cref="IssueInvoiceHandler"/>. <see cref="ProviderInvoiceNumber"/> is
/// <c>string?</c> (NOT via <see cref="DocumentRef"/>) so a stored-null number on the
/// idempotent-hit path round-trips as JSON <c>null</c> instead of being coalesced to
/// <c>""</c>. <see cref="WasIdempotentHit"/> lets the endpoint pick the branch-divergent
/// audit op-name and <c>kontrahentId</c> source without changing any wire field.
/// </summary>
public sealed record IssueInvoiceResult(
    int ProviderInvoiceId,
    string? ProviderInvoiceNumber,
    bool WasIdempotentHit);
