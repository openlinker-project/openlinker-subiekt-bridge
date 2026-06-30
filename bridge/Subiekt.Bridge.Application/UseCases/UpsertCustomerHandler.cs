using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;
using Subiekt.Bridge.Domain.Customers;

namespace Subiekt.Bridge.Application.UseCases;

/// <summary>
/// Upsert-customer use case. Validates the NIP (when supplied) and the customer
/// invariants through the Domain, then delegates persistence to
/// <see cref="ICustomerDirectory"/>. Pure orchestration — no framework deps.
/// </summary>
public sealed class UpsertCustomerHandler
{
    private readonly ICustomerDirectory _directory;

    public UpsertCustomerHandler(ICustomerDirectory directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    public async Task<Result<CustomerRef>> HandleAsync(
        UpsertCustomerCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // NIP is optional. When present it must pass Domain validation; we keep the
        // validated value object so the adapter can rely on a clean 10-digit NIP.
        Nip? nip = null;
        if (!string.IsNullOrWhiteSpace(command.Nip))
        {
            var nipResult = Nip.TryCreate(command.Nip);
            if (nipResult.IsFailure)
                return Result.Failure<CustomerRef>(nipResult.Error);
            nip = nipResult.Value;
        }

        var address = command.Address is null
            ? null
            : new Address(
                command.Address.Ulica,
                command.Address.NrDomu,
                command.Address.NrLokalu,
                command.Address.KodPocztowy,
                command.Address.Miejscowosc,
                command.Address.Poczta,
                command.Address.CountryCode);

        var customerResult = Customer.Create(
            command.Name,
            nip,
            command.IsCompany,
            command.Contact,
            address);

        if (customerResult.IsFailure)
            return Result.Failure<CustomerRef>(customerResult.Error);

        return await _directory.UpsertAsync(customerResult.Value, cancellationToken);
    }
}
