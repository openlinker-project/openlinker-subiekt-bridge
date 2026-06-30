using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Domain.Customers;

/// <summary>
/// Customer aggregate (Subiekt "kontrahent"). Created through the invariant-checking
/// <see cref="Create"/> factory so an invalid customer can never exist.
/// <para>
/// Invariants ported from the legacy <c>PodmiotyService</c>:
/// a short name (NazwaSkrocona) is always required; a company is the default; the
/// NIP is optional but, when present, must already be a validated <see cref="Customers.Nip"/>.
/// </para>
/// </summary>
public sealed class Customer
{
    private Customer(
        string nazwaSkrocona,
        Nip? nip,
        bool isCompany,
        string? contact,
        Address? address)
    {
        NazwaSkrocona = nazwaSkrocona;
        Nip = nip;
        IsCompany = isCompany;
        Contact = contact;
        Address = address;
    }

    /// <summary>Short display name; the business key in Subiekt.</summary>
    public string NazwaSkrocona { get; }

    /// <summary>Optional validated tax id. Drives NIP-based idempotency in the adapter.</summary>
    public Nip? Nip { get; }

    /// <summary>True for a company (firma), false for a natural person (osoba).</summary>
    public bool IsCompany { get; }

    /// <summary>Optional contact (phone/e-mail), free text.</summary>
    public string? Contact { get; }

    /// <summary>Optional structured address.</summary>
    public Address? Address { get; }

    public static Result<Customer> Create(
        string? nazwaSkrocona,
        Nip? nip,
        bool isCompany,
        string? contact = null,
        Address? address = null)
    {
        if (string.IsNullOrWhiteSpace(nazwaSkrocona))
            return Result.Failure<Customer>(
                new Error("customer.name", "Customer short name (NazwaSkrocona) is required."));

        var normalizedContact = string.IsNullOrWhiteSpace(contact) ? null : contact.Trim();
        var effectiveAddress = address is { IsEmpty: true } ? null : address;

        return Result.Success(new Customer(
            nazwaSkrocona.Trim(),
            nip,
            isCompany,
            normalizedContact,
            effectiveAddress));
    }
}
