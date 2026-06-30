namespace Subiekt.Bridge.Application.UseCases;

/// <summary>
/// Command to upsert a customer. Carries raw input (the NIP is still a string here
/// and is validated inside the handler). The structured address is optional.
/// </summary>
public sealed record UpsertCustomerCommand(
    string Name,
    string? Nip,
    bool IsCompany,
    string? Contact = null,
    UpsertCustomerAddress? Address = null);

/// <summary>Raw address input for <see cref="UpsertCustomerCommand"/>.</summary>
public sealed record UpsertCustomerAddress(
    string? Ulica = null,
    string? NrDomu = null,
    string? NrLokalu = null,
    string? KodPocztowy = null,
    string? Miejscowosc = null,
    string? Poczta = null,
    string CountryCode = "PL");
