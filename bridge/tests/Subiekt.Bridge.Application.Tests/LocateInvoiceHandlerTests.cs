using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Application.UseCases;
using Subiekt.Bridge.Domain.Common;
using Xunit;

namespace Subiekt.Bridge.Application.Tests;

/// <summary>
/// Drives <see cref="LocateInvoiceHandler"/> against typed fakes, mirroring
/// <see cref="IssueInvoiceHandlerTests"/>. The load-bearing case is the one that
/// separates this handler from the issue path: a store READ FAULT must surface as
/// a failure, never as a clean "not found" — the caller acts on an absence by
/// issuing, so a fault reported as absence produces the duplicate permanent fiscal
/// document this whole path exists to prevent.
/// </summary>
public class LocateInvoiceHandlerTests
{
    private sealed class FakeIdempotencyStore : IIdempotencyStore
    {
        public string? LastKey { get; private set; }

        public Result<IdempotentInvoice?> NextTryGet { get; set; } = Result.Success<IdempotentInvoice?>(null);

        public Task<Result<IdempotentInvoice?>> TryGetAsync(string key, CancellationToken cancellationToken = default)
        {
            LastKey = key;
            return Task.FromResult(NextTryGet);
        }

        public Task<Result> StoreAsync(string key, IdempotentInvoice invoice, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success());
    }

    private sealed class FakeStatusReader : IDocumentStatusReader
    {
        public int CallCount { get; private set; }

        public int? LastId { get; private set; }

        public Result<DocumentStatus> Next { get; set; } =
            Result.Success(new DocumentStatus(0, "zatwierdzony", null, null, null, null, null, null));

        public Task<Result<DocumentStatus>> GetStatusAsync(int documentId, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastId = documentId;
            return Task.FromResult(Next);
        }
    }

    private static DocumentStatus Status(string state, string? numer = null, KsefStatus? ksef = null) =>
        new(77, state, numer, null, null, null, null, ksef);

    [Fact]
    public async Task Unknown_key_is_a_clean_absence_not_an_error()
    {
        var store = new FakeIdempotencyStore { NextTryGet = Result.Success<IdempotentInvoice?>(null) };
        var reader = new FakeStatusReader();

        var result = await new LocateInvoiceHandler(store, reader).HandleAsync("ol-key-1");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
        // No point reading a status for a document we never recorded.
        Assert.Equal(0, reader.CallCount);
    }

    [Fact]
    public async Task A_store_read_fault_fails_and_is_never_reported_as_not_found()
    {
        var store = new FakeIdempotencyStore
        {
            NextTryGet = Result.Failure<IdempotentInvoice?>(new Error("io", "disk went away")),
        };

        var result = await new LocateInvoiceHandler(store, new FakeStatusReader()).HandleAsync("ol-key-1");

        Assert.True(result.IsFailure);
        // `unreachable` is what the endpoint maps to 503, i.e. retry — as opposed
        // to a 422 the caller would read as a settled business answer.
        Assert.Equal("unreachable", result.Error.Code);
    }

    [Fact]
    public async Task A_hit_returns_the_document_and_its_clearance_fields()
    {
        var store = new FakeIdempotencyStore
        {
            NextTryGet = Result.Success<IdempotentInvoice?>(new IdempotentInvoice(77, "FS 1/2026")),
        };
        var reader = new FakeStatusReader
        {
            Next = Result.Success(Status("zatwierdzony", "FS 1/2026", new KsefStatus("accepted", true, "KSEF-9"))),
        };

        var result = await new LocateInvoiceHandler(store, reader).HandleAsync("ol-key-1");

        Assert.True(result.IsSuccess);
        var located = Assert.IsType<LocatedInvoice>(result.Value);
        Assert.Equal(77, located.ProviderInvoiceId);
        Assert.Equal("FS 1/2026", located.Numer);
        Assert.Equal("accepted", located.RegulatoryStatus);
        Assert.Equal("KSEF-9", located.ClearanceReference);
        Assert.Equal(77, reader.LastId);
    }

    [Fact]
    public async Task The_key_is_namespaced_exactly_as_the_issue_path_namespaces_it()
    {
        var store = new FakeIdempotencyStore();

        await new LocateInvoiceHandler(store, new FakeStatusReader()).HandleAsync("ol-key-1");

        // Drift here would make a key that short-circuits a retried issue
        // invisible to the locate, which is the one thing the two must agree on.
        Assert.Equal(IdempotencyKeyPrefixes.Fv + "ol-key-1", store.LastKey);
    }

    [Fact]
    public async Task A_remembered_id_whose_document_is_gone_reads_as_an_absence()
    {
        var store = new FakeIdempotencyStore
        {
            NextTryGet = Result.Success<IdempotentInvoice?>(new IdempotentInvoice(77, "FS 1/2026")),
        };
        var reader = new FakeStatusReader { Next = Result.Success(Status("not_found")) };

        var result = await new LocateInvoiceHandler(store, reader).HandleAsync("ol-key-1");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task A_status_read_fault_fails_rather_than_reporting_an_absence()
    {
        var store = new FakeIdempotencyStore
        {
            NextTryGet = Result.Success<IdempotentInvoice?>(new IdempotentInvoice(77, "FS 1/2026")),
        };
        var reader = new FakeStatusReader
        {
            Next = Result.Failure<DocumentStatus>(new Error("unreachable", "SQL down")),
        };

        var result = await new LocateInvoiceHandler(store, reader).HandleAsync("ol-key-1");

        Assert.True(result.IsFailure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_key_is_refused_without_touching_the_store(string key)
    {
        var store = new FakeIdempotencyStore();

        var result = await new LocateInvoiceHandler(store, new FakeStatusReader()).HandleAsync(key);

        Assert.True(result.IsFailure);
        Assert.Equal("bad_request", result.Error.Code);
        Assert.Null(store.LastKey);
    }

    [Fact]
    public async Task A_missing_ksef_block_reads_as_none_rather_than_null()
    {
        var store = new FakeIdempotencyStore
        {
            NextTryGet = Result.Success<IdempotentInvoice?>(new IdempotentInvoice(77, "FS 1/2026")),
        };
        var reader = new FakeStatusReader { Next = Result.Success(Status("zatwierdzony", "FS 1/2026")) };

        var result = await new LocateInvoiceHandler(store, reader).HandleAsync("ol-key-1");

        var located = Assert.IsType<LocatedInvoice>(result.Value);
        Assert.Equal("none", located.RegulatoryStatus);
        Assert.Null(located.ClearanceReference);
    }
}
