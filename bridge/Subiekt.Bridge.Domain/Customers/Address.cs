namespace Subiekt.Bridge.Domain.Customers;

/// <summary>
/// A postal address value object. Mirrors the fields the legacy
/// <c>AddressDto</c>/Subiekt <c>AdresSzczegoly</c> path consumes
/// (Ulica/NrDomu/NrLokalu/KodPocztowy/Miejscowosc/Poczta) plus a country code.
/// All fields are optional except <see cref="CountryCode"/>, which defaults to "PL".
/// </summary>
public sealed record Address
{
    public Address(
        string? ulica = null,
        string? nrDomu = null,
        string? nrLokalu = null,
        string? kodPocztowy = null,
        string? miejscowosc = null,
        string? poczta = null,
        string countryCode = "PL")
    {
        Ulica = Normalize(ulica);
        NrDomu = Normalize(nrDomu);
        NrLokalu = Normalize(nrLokalu);
        KodPocztowy = Normalize(kodPocztowy);
        Miejscowosc = Normalize(miejscowosc);
        Poczta = Normalize(poczta);
        CountryCode = string.IsNullOrWhiteSpace(countryCode) ? "PL" : countryCode.Trim().ToUpperInvariant();
    }

    public string? Ulica { get; }
    public string? NrDomu { get; }
    public string? NrLokalu { get; }
    public string? KodPocztowy { get; }
    public string? Miejscowosc { get; }
    public string? Poczta { get; }
    public string CountryCode { get; }

    /// <summary>True when no address line carries any content.</summary>
    public bool IsEmpty =>
        Ulica is null && NrDomu is null && NrLokalu is null &&
        KodPocztowy is null && Miejscowosc is null && Poczta is null;

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
