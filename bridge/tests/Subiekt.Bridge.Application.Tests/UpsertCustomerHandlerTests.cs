using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Application.UseCases;
using Subiekt.Bridge.Domain.Common;
using Subiekt.Bridge.Domain.Customers;
using Xunit;

namespace Subiekt.Bridge.Application.Tests;

public class UpsertCustomerHandlerTests
{
    /// <summary>In-memory fake adapter: records calls and assigns sequential ids by NIP.</summary>
    private sealed class FakeCustomerDirectory : ICustomerDirectory
    {
        private readonly Dictionary<string, CustomerRef> _byNip = new();
        private int _nextId = 1;

        public int UpsertCallCount { get; private set; }

        public Customer? LastUpserted { get; private set; }

        public Task<Result<CustomerRef>> UpsertAsync(Customer customer, CancellationToken cancellationToken = default)
        {
            UpsertCallCount++;
            LastUpserted = customer;

            var key = customer.Nip?.Value ?? Guid.NewGuid().ToString("N");
            if (!_byNip.TryGetValue(key, out var existing))
            {
                existing = new CustomerRef(_nextId++, $"K/{key}");
                _byNip[key] = existing;
            }

            return Task.FromResult(Result.Success(existing));
        }

        public Task<Result<CustomerRef?>> FindByNipAsync(Nip nip, CancellationToken cancellationToken = default)
        {
            var found = _byNip.TryGetValue(nip.Value, out var r) ? (CustomerRef?)r : null;
            return Task.FromResult(Result.Success(found));
        }
    }

    [Fact]
    public async Task HandleAsync_ValidCompanyWithNip_UpsertsAndReturnsRef()
    {
        var fake = new FakeCustomerDirectory();
        var handler = new UpsertCustomerHandler(fake);
        var command = new UpsertCustomerCommand(
            Name: "ACME Sp. z o.o.",
            Nip: "5260001246",
            IsCompany: true,
            Contact: "123-456-789",
            Address: new UpsertCustomerAddress(Ulica: "Marszałkowska", NrDomu: "1", Miejscowosc: "Warszawa"));

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.Equal(1, result.Value.Id);
        Assert.Equal(1, fake.UpsertCallCount);
        Assert.NotNull(fake.LastUpserted);
        Assert.True(fake.LastUpserted!.IsCompany);
        Assert.Equal("5260001246", fake.LastUpserted.Nip?.Value);
        Assert.Equal("Warszawa", fake.LastUpserted.Address?.Miejscowosc);
    }

    [Fact]
    public async Task HandleAsync_NoNip_StillUpserts()
    {
        var fake = new FakeCustomerDirectory();
        var handler = new UpsertCustomerHandler(fake);
        var command = new UpsertCustomerCommand("Jan Kowalski", Nip: null, IsCompany: false);

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, fake.UpsertCallCount);
        Assert.False(fake.LastUpserted!.IsCompany);
        Assert.Null(fake.LastUpserted.Nip);
    }

    [Fact]
    public async Task HandleAsync_InvalidNip_RejectedAndAdapterNotCalled()
    {
        var fake = new FakeCustomerDirectory();
        var handler = new UpsertCustomerHandler(fake);
        var command = new UpsertCustomerCommand("ACME", Nip: "5260001245", IsCompany: true); // bad checksum

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsFailure);
        Assert.Equal("nip.checksum", result.Error.Code);
        Assert.Equal(0, fake.UpsertCallCount); // domain validation blocked the call
    }

    [Fact]
    public async Task HandleAsync_BlankName_RejectedAndAdapterNotCalled()
    {
        var fake = new FakeCustomerDirectory();
        var handler = new UpsertCustomerHandler(fake);
        var command = new UpsertCustomerCommand("   ", Nip: "5260001246", IsCompany: true);

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsFailure);
        Assert.Equal("customer.name", result.Error.Code);
        Assert.Equal(0, fake.UpsertCallCount);
    }
}
