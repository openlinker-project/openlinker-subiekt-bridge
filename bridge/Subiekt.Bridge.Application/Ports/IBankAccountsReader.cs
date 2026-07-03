using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Application.Ports;

/// <summary>
/// A seller (MojaFirma) bank account row, as configured under Subiekt's
/// "Konfiguracja systemu -&gt; Rachunki bankowe". <see cref="IsDefault"/> mirrors
/// the UI's "Podstawowy" column (the owner's primary account back-reference),
/// NOT the per-currency <c>PodstawowyDlaWaluty</c> flag.
/// <see cref="OwnerPodmiotId"/>/<see cref="OwnerName"/> identify which seller
/// Podmiot owns the account — an install may have more than one (issue #3).
/// </summary>
public sealed record BankAccountView(
    int Id,
    string? Nazwa,
    string? Numer,
    string? NumerBanku,
    string? Opis,
    string? Waluta,
    bool JestRachunkiemVAT,
    bool IsDefault,
    int OwnerPodmiotId,
    string? OwnerName);

/// <summary>
/// Read-only port over the seller's bank accounts (issue #1). Implemented by the
/// SQL adapter against a non-Sfera-locked connection (3A read pattern).
/// </summary>
public interface IBankAccountsReader
{
    /// <summary>List the seller's ACTIVE bank accounts, default account first.</summary>
    Task<Result<IReadOnlyList<BankAccountView>>> ListAsync(CancellationToken cancellationToken = default);
}
