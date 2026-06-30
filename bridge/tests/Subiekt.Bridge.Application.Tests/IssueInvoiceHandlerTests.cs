using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Application.UseCases;
using Subiekt.Bridge.Domain.Common;
using Subiekt.Bridge.Domain.Invoices;
using Xunit;

namespace Subiekt.Bridge.Application.Tests;

/// <summary>
/// Drives <see cref="IssueInvoiceHandler"/> against typed fakes, mirroring
/// <see cref="UpsertCustomerHandlerTests"/>. Covers the idempotency policy: gate-before-
/// build ordering (F4), fall-through on any non-hit — miss OR read-fault (F5), and the
/// swallowed store-write fault (Decision 5).
/// </summary>
public class IssueInvoiceHandlerTests
{
    /// <summary>Records issue calls; returns a configurable success/failure.</summary>
    private sealed class FakeIssuer : IIssueInvoiceWithBuyer
    {
        public int IssueCallCount { get; private set; }

        public Result<DocumentRef> Next { get; set; } = Result.Success(new DocumentRef(0, string.Empty));

        public Task<Result<DocumentRef>> IssueAsync(
            SalesDocument document,
            InlineBuyer? buyer,
            CancellationToken cancellationToken = default)
        {
            IssueCallCount++;
            return Task.FromResult(Next);
        }
    }

    /// <summary>Records read/write calls; returns configurable TryGet/Store results.</summary>
    private sealed class FakeIdempotencyStore : IIdempotencyStore
    {
        public int TryGetCallCount { get; private set; }

        public int StoreCallCount { get; private set; }

        public Result<IdempotentInvoice?> NextTryGet { get; set; } = Result.Success<IdempotentInvoice?>(null);

        public Result NextStore { get; set; } = Result.Success();

        public Task<Result<IdempotentInvoice?>> TryGetAsync(string key, CancellationToken cancellationToken = default)
        {
            TryGetCallCount++;
            return Task.FromResult(NextTryGet);
        }

        public Task<Result> StoreAsync(string key, IdempotentInvoice invoice, CancellationToken cancellationToken = default)
        {
            StoreCallCount++;
            return Task.FromResult(NextStore);
        }
    }

    /// <summary>A minimal valid document so BuildDocument can return a success.</summary>
    private static SalesDocument SampleDocument()
    {
        var vat = VatRate.TryCreate("23");
        Assert.True(vat.IsSuccess, vat.Error.ToString());
        var line = new InvoiceLine("P-1", 1m, new Money(123m, "PLN"), vat.Value);
        var doc = SalesDocument.Create(DocumentType.FV, buyerId: 7, "PLN", DateTimeOffset.UnixEpoch, new[] { line });
        Assert.True(doc.IsSuccess, doc.Error.ToString());
        return doc.Value;
    }

    /// <summary>A BuildDocument delegate that records whether it ran and returns success.</summary>
    private static Func<Result<(SalesDocument, InlineBuyer?)>> SuccessBuild(Action onInvoke)
        => () =>
        {
            onInvoke();
            return Result.Success<(SalesDocument, InlineBuyer?)>((SampleDocument(), null));
        };

    // Case 1: fresh issue (key present, miss) -> BuildDocument invoked -> stores
    // IdempotentInvoice(id, numer) -> IssueInvoiceResult(id, numer, WasIdempotentHit:false).
    [Fact]
    public async Task HandleAsync_KeyPresentMiss_BuildsIssuesAndStores()
    {
        var issuer = new FakeIssuer { Next = Result.Success(new DocumentRef(42, "FV/2026/01")) };
        var store = new FakeIdempotencyStore { NextTryGet = Result.Success<IdempotentInvoice?>(null) };
        var handler = new IssueInvoiceHandler(issuer, store);
        var built = 0;
        var command = new IssueInvoiceCommand("key-1", SuccessBuild(() => built++));

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.Equal(1, built);
        Assert.Equal(1, store.TryGetCallCount);
        Assert.Equal(1, issuer.IssueCallCount);
        Assert.Equal(1, store.StoreCallCount);
        Assert.Equal(42, result.Value.ProviderInvoiceId);
        Assert.Equal("FV/2026/01", result.Value.ProviderInvoiceNumber);
        Assert.False(result.Value.WasIdempotentHit);
    }

    // Case 2: retried key (real hit) -> returns prior id/number, WasIdempotentHit:true,
    // WITHOUT calling the issuer.
    [Fact]
    public async Task HandleAsync_RetriedKeyHit_ReturnsPriorWithoutIssuing()
    {
        var issuer = new FakeIssuer();
        var store = new FakeIdempotencyStore
        {
            NextTryGet = Result.Success<IdempotentInvoice?>(new IdempotentInvoice(99, "FV/2026/99"))
        };
        var handler = new IssueInvoiceHandler(issuer, store);
        var built = 0;
        var command = new IssueInvoiceCommand("key-2", SuccessBuild(() => built++));

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsSuccess);
        Assert.Equal(99, result.Value.ProviderInvoiceId);
        Assert.Equal("FV/2026/99", result.Value.ProviderInvoiceNumber);
        Assert.True(result.Value.WasIdempotentHit);
        Assert.Equal(0, issuer.IssueCallCount);
        Assert.Equal(0, store.StoreCallCount);
        Assert.Equal(0, built);
    }

    // Case 3: hit with a stored null number -> result ProviderInvoiceNumber is null, NOT "" (F1).
    [Fact]
    public async Task HandleAsync_HitWithNullNumber_PreservesNull()
    {
        var issuer = new FakeIssuer();
        var store = new FakeIdempotencyStore
        {
            NextTryGet = Result.Success<IdempotentInvoice?>(new IdempotentInvoice(7, null))
        };
        var handler = new IssueInvoiceHandler(issuer, store);
        var command = new IssueInvoiceCommand("key-3", SuccessBuild(() => { }));

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.WasIdempotentHit);
        Assert.Null(result.Value.ProviderInvoiceNumber);
    }

    // Case 4: issuer failure -> propagates the failure and does NOT call StoreAsync.
    [Fact]
    public async Task HandleAsync_IssuerFailure_PropagatesAndDoesNotStore()
    {
        var issuer = new FakeIssuer { Next = Result.Failure<DocumentRef>(new Error("rejected", "boom")) };
        var store = new FakeIdempotencyStore { NextTryGet = Result.Success<IdempotentInvoice?>(null) };
        var handler = new IssueInvoiceHandler(issuer, store);
        var command = new IssueInvoiceCommand("key-4", SuccessBuild(() => { }));

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsFailure);
        Assert.Equal("rejected", result.Error.Code);
        Assert.Equal(1, issuer.IssueCallCount);
        Assert.Equal(0, store.StoreCallCount);
    }

    // Case 5: no-key path -> never touches the store (neither TryGet nor Store),
    // BuildDocument IS invoked, returns WasIdempotentHit:false.
    [Fact]
    public async Task HandleAsync_NoKey_NeverTouchesStore()
    {
        var issuer = new FakeIssuer { Next = Result.Success(new DocumentRef(5, "FV/2026/05")) };
        var store = new FakeIdempotencyStore();
        var handler = new IssueInvoiceHandler(issuer, store);
        var built = 0;
        var command = new IssueInvoiceCommand(IdempotencyKey: null, SuccessBuild(() => built++));

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.WasIdempotentHit);
        Assert.Equal(1, built);
        Assert.Equal(1, issuer.IssueCallCount);
        Assert.Equal(0, store.TryGetCallCount);
        Assert.Equal(0, store.StoreCallCount);
    }

    // Case 6 (F5): TryGetAsync read-FAULT falls through to a fresh issue -> BuildDocument
    // invoked, issuer IS called, result is fresh (WasIdempotentHit:false), NOT a propagated failure.
    [Fact]
    public async Task HandleAsync_TryGetReadFault_FallsThroughToFreshIssue()
    {
        var issuer = new FakeIssuer { Next = Result.Success(new DocumentRef(11, "FV/2026/11")) };
        var store = new FakeIdempotencyStore
        {
            NextTryGet = Result.Failure<IdempotentInvoice?>(new Error("unreachable", "store down"))
        };
        var handler = new IssueInvoiceHandler(issuer, store);
        var built = 0;
        var command = new IssueInvoiceCommand("key-6", SuccessBuild(() => built++));

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.False(result.Value.WasIdempotentHit);
        Assert.Equal(11, result.Value.ProviderInvoiceId);
        Assert.Equal(1, built);
        Assert.Equal(1, issuer.IssueCallCount);
        Assert.Equal(1, store.StoreCallCount);
    }

    // Case 7 (F4): real hit short-circuits BuildDocument -> the delegate is NEVER invoked.
    [Fact]
    public async Task HandleAsync_RealHit_DoesNotInvokeBuildDocument()
    {
        var issuer = new FakeIssuer();
        var store = new FakeIdempotencyStore
        {
            NextTryGet = Result.Success<IdempotentInvoice?>(new IdempotentInvoice(1, "FV/2026/01"))
        };
        var handler = new IssueInvoiceHandler(issuer, store);
        var built = 0;
        var command = new IssueInvoiceCommand("key-7", SuccessBuild(() => built++));

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, built);
        Assert.Equal(0, issuer.IssueCallCount);
    }

    // Case 8 (F4): build failure on a non-hit -> returns that failure, does NOT call the
    // issuer, does NOT store. The failed STAGE is attributed via BuildFailedCode so the
    // endpoint can map it to 422 deterministically; the arbitrary DOMAIN code that the
    // mapper raised ("vatRate" here) is re-wrapped but the original message is preserved.
    [Fact]
    public async Task HandleAsync_BuildFailureOnNonHit_PropagatesWithBuildStageCodeWithoutIssuingOrStoring()
    {
        var issuer = new FakeIssuer();
        var store = new FakeIdempotencyStore { NextTryGet = Result.Success<IdempotentInvoice?>(null) };
        var handler = new IssueInvoiceHandler(issuer, store);
        var command = new IssueInvoiceCommand(
            "key-8",
            () => Result.Failure<(SalesDocument, InlineBuyer?)>(new Error("vatRate", "bad request")));

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsFailure);
        Assert.Equal(IssueInvoiceHandler.BuildFailedCode, result.Error.Code);
        Assert.Equal("bad request", result.Error.Message);
        Assert.Equal(0, issuer.IssueCallCount);
        Assert.Equal(0, store.StoreCallCount);
    }

    // Case 9 (Decision 5): store-write fault is swallowed -> still returns
    // Result.Success(IssueInvoiceResult(..., WasIdempotentHit:false)).
    [Fact]
    public async Task HandleAsync_StoreWriteFault_IsSwallowedAndStillSucceeds()
    {
        var issuer = new FakeIssuer { Next = Result.Success(new DocumentRef(21, "FV/2026/21")) };
        var store = new FakeIdempotencyStore
        {
            NextTryGet = Result.Success<IdempotentInvoice?>(null),
            NextStore = Result.Failure(new Error("unreachable", "store down"))
        };
        var handler = new IssueInvoiceHandler(issuer, store);
        var command = new IssueInvoiceCommand("key-9", SuccessBuild(() => { }));

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.False(result.Value.WasIdempotentHit);
        Assert.Equal(21, result.Value.ProviderInvoiceId);
        Assert.Equal(1, store.StoreCallCount);
    }
}
