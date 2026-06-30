using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;
using Subiekt.Bridge.Domain.Customers;

namespace Subiekt.Bridge.Infrastructure.Sfera.Adapters;

/// <summary>
/// <see cref="ICustomerDirectory"/> implemented over the moved
/// <see cref="SferaPodmiotyService"/>, routed through <see cref="SferaWriteQueue"/>
/// so the upsert runs serially on the single write worker. Replaces the temporary
/// <c>LegacyPodmiotyCustomerDirectory</c> that lived in Api.
/// <para>
/// The Domain already decided firma vs osoba (<see cref="Customer.IsCompany"/>); we
/// route to the matching factory and translate the validated <see cref="Customer"/>
/// into the Infrastructure-local <see cref="SferaCustomerInput"/>. Exceptions from the
/// Sfera path are classified into the bridge's two error classes and returned as a
/// domain <see cref="Result"/> failure rather than escaping the port.
/// </para>
/// </summary>
public sealed class SferaCustomerDirectory : ICustomerDirectory
{
    private readonly SferaWriteQueue _queue;
    private readonly SferaPodmiotyService _podmioty;

    public SferaCustomerDirectory(SferaWriteQueue queue, SferaPodmiotyService podmioty)
    {
        _queue = queue;
        _podmioty = podmioty;
    }

    public async Task<Result<CustomerRef>> UpsertAsync(Customer customer, CancellationToken cancellationToken = default)
    {
        var input = ToInput(customer);
        try
        {
            var (id, numer) = await _queue.EnqueueAsync(session =>
                customer.IsCompany
                    ? _podmioty.UpsertFirma(session, input)
                    : _podmioty.UpsertOsoba(session, input),
                cancellationToken).ConfigureAwait(false);

            return Result.Success(new CustomerRef(id, numer));
        }
        catch (Exception ex)
        {
            var bex = BridgeException.Classify(ex);
            return Result.Failure<CustomerRef>(new Error(bex.CodeString, bex.Reason));
        }
    }

    public async Task<Result<CustomerRef?>> FindByNipAsync(Nip nip, CancellationToken cancellationToken = default)
    {
        try
        {
            var found = await _queue.EnqueueAsync(session => _podmioty.WyszukajWgNIP(session, nip.Value), cancellationToken)
                .ConfigureAwait(false);
            if (found is null)
                return Result.Success<CustomerRef?>(null);

            return Result.Success<CustomerRef?>(new CustomerRef(found.Value.Id, found.Value.Numer));
        }
        catch (Exception ex)
        {
            var bex = BridgeException.Classify(ex);
            return Result.Failure<CustomerRef?>(new Error(bex.CodeString, bex.Reason));
        }
    }

    internal static SferaCustomerInput ToInput(Customer customer)
    {
        SferaAddressInput? address = customer.Address is null
            ? null
            : new SferaAddressInput(
                Ulica: customer.Address.Ulica,
                NrDomu: customer.Address.NrDomu,
                NrLokalu: customer.Address.NrLokalu,
                KodPocztowy: customer.Address.KodPocztowy,
                Miejscowosc: customer.Address.Miejscowosc,
                Poczta: customer.Address.Poczta);

        return new SferaCustomerInput(
            NazwaSkrocona: customer.NazwaSkrocona,
            NIP: customer.Nip?.Value,
            Telefon: customer.Contact,
            Aktywny: true,
            Address: address);
    }
}
