using SferaApi.Models;
using Subiekt.Bridge.Application.UseCases;

namespace SferaApi.Contracts;

/// <summary>
/// Stable request/response shapes for the upsert-customer endpoint. These are the
/// bridge's own contract — they deliberately do NOT expose the Subiekt DB schema.
/// The endpoint still binds the legacy <see cref="CreateFirmaRequestDto"/> on the
/// wire so the existing cockpit keeps working; <see cref="FromLegacy"/> maps it
/// onto the Application command.
/// </summary>
public sealed record UpsertCustomerResponse(int Id, string Numer, string NazwaSkrocona, string? Nip);

/// <summary>Maps the legacy DTO shapes onto the Application use-case command.</summary>
public static class UpsertCustomerContractMapper
{
    public static UpsertCustomerCommand FromLegacy(CreateFirmaRequestDto dto)
    {
        // Legacy "Typ" is "firma" | "osoba"; everything other than "osoba" is a company.
        var isCompany = !string.Equals(dto.Typ, "osoba", StringComparison.OrdinalIgnoreCase);

        UpsertCustomerAddress? address = dto.Address is null
            ? null
            : new UpsertCustomerAddress(
                Ulica: dto.Address.Ulica,
                NrDomu: dto.Address.NrDomu,
                NrLokalu: dto.Address.NrLokalu,
                KodPocztowy: dto.Address.KodPocztowy,
                Miejscowosc: dto.Address.Miejscowosc,
                Poczta: dto.Address.Poczta,
                CountryCode: string.IsNullOrWhiteSpace(dto.Address.CountryCode) ? "PL" : dto.Address.CountryCode);

        return new UpsertCustomerCommand(
            Name: dto.NazwaSkrocona,
            Nip: dto.NIP,
            IsCompany: isCompany,
            Contact: dto.Telefon,
            Address: address);
    }
}
