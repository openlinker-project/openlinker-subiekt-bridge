using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Infrastructure.Sfera.Adapters;

/// <summary>
/// <see cref="IInvoicePdfRenderer"/> implemented over <see cref="SferaPdfPrintoutService"/>,
/// routed through the single-writer <see cref="SferaWriteQueue"/> so the headless
/// render never runs concurrently with an issue/correction (Sfera's <c>Uchwyt</c>
/// is not thread-safe). Maps failures onto the bridge error contract: a missing
/// document id -> <c>not_found</c> (404); transient infra -> <c>unreachable</c> (503);
/// everything else -> <c>rejected</c> (422).
/// </summary>
public sealed class SferaInvoicePdfRenderer : IInvoicePdfRenderer
{
    private readonly SferaWriteQueue _queue;
    private readonly SferaPdfPrintoutService _printout;

    public SferaInvoicePdfRenderer(SferaWriteQueue queue, SferaPdfPrintoutService printout)
    {
        _queue = queue;
        _printout = printout;
    }

    public async Task<Result<byte[]>> RenderAsync(int documentId, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await _queue
                .EnqueueAsync(session => _printout.Render(session, documentId), cancellationToken)
                .ConfigureAwait(false);
            return Result.Success(bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller (browser) went away. Propagate cancellation rather than mislabel it
            // as a business rejection (422) — there is no response to send anyway.
            throw;
        }
        catch (Exception ex)
        {
            // Unwrap reflection's TargetInvocationException so the inner type is classified.
            var inner = ex;
            while (inner is System.Reflection.TargetInvocationException && inner.InnerException != null)
                inner = inner.InnerException;

            return inner switch
            {
                // Missing document id -> 404.
                SferaPdfPrintoutService.DocumentNotFoundException =>
                    Result.Failure<byte[]>(new Error("not_found", $"Dokument {documentId} nie istnieje.")),
                // Print-pipeline glitch (no handle / no PDF / bad magic / timeout) is a
                // transient subsystem failure -> unreachable (503, retryable), not 422.
                // Carry the SPECIFIC detail (e.g. "render timed out after 120s for doc N")
                // as the Error.Message so the endpoint's audit log records the real cause;
                // the client still gets the generic canned reason via BridgeException.Reason.
                SferaPdfPrintoutService.RenderFailedException =>
                    Result.Failure<byte[]>(new Error("unreachable", inner.Message)),
                // SQL/session/connectivity vs genuine business rejection.
                _ => MapClassified(ex),
            };
        }
    }

    private static Result<byte[]> MapClassified(Exception ex)
    {
        var bex = BridgeException.Classify(ex);
        return Result.Failure<byte[]>(new Error(bex.CodeString, bex.Reason));
    }
}
