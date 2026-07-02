namespace Subiekt.Bridge.Infrastructure.Sfera;

// Infrastructure-local input shapes for the moved Sfera services. These replace the
// dependency the legacy services had on the Api's SferaApi.Models DTOs
// (CreateFirmaRequestDto/AddressDto/CreateInvoiceLineRequestDto/...). Infrastructure
// must NOT reference Api, so the port adapters translate Domain objects into these.
// They mirror exactly the fields each service consumed.

/// <summary>Address fields consumed by the Podmioty address path (AdresSzczegoly).</summary>
public sealed record SferaAddressInput(
    string? Ulica = null,
    string? NrDomu = null,
    string? NrLokalu = null,
    string? KodPocztowy = null,
    string? Miejscowosc = null,
    string? Poczta = null);

/// <summary>Customer upsert input (firma/osoba), mirroring the legacy CreateFirmaRequestDto fields.</summary>
public sealed record SferaCustomerInput(
    string NazwaSkrocona,
    string? NIP,
    string? Telefon,
    bool Aktywny,
    SferaAddressInput? Address);

/// <summary>One sales-document line, mirroring CreateInvoiceLineRequestDto.</summary>
public sealed record SferaInvoiceLineInput(
    string TowarSymbol,
    decimal Ilosc,
    decimal CenaBrutto,
    string StawkaVAT,
    string? Name);

/// <summary>
/// Sales-document input. Fiscal dates are pre-computed by the adapter via
/// <c>Domain.SalesDocument.ComputeFiscalDates(IClock)</c>: <see cref="DataSprzedazy"/> is the
/// sale/VAT month (may be in the past) and <see cref="DataWydania"/> is the dispatch/entry
/// date. Lines are already discount-folded by the adapter (no negative-price lines remain).
/// </summary>
public sealed record SferaInvoiceInput(
    int KontrahentId,
    DateTime DataSprzedazy,
    DateTime DataWydania,
    IReadOnlyList<SferaInvoiceLineInput> Lines,
    SferaPaymentInput? Payment = null,
    SferaBranchInput? Branch = null);

/// <summary>
/// Explicit payment selection for a sales document (issue #1). <see cref="Method"/>
/// is the neutral "cash" | "transfer"; <see cref="BankAccountId"/> is the Subiekt
/// RachunekBankowy id (transfer only); <see cref="Currency"/> is the document
/// currency used for the account-currency pre-check.
/// </summary>
public sealed record SferaPaymentInput(string Method, int? BankAccountId, string Currency);

/// <summary>
/// Explicit Oddzial/Stanowisko Kasowe selection for a sales document (issue #5).
/// <see cref="OddzialId"/> is the Subiekt <c>JednostkaOrganizacyjna</c> id (null =
/// document's implicit-default branch); <see cref="StanowiskoKasoweId"/> is the Subiekt
/// <c>CentraGromadzeniaFinansow_StanowiskoKasowe</c> id (always set - the Domain
/// <c>BranchSelection</c> rule rejects Oddzial-without-Stanowisko upstream).
/// </summary>
public sealed record SferaBranchInput(int? OddzialId, int StanowiskoKasoweId);

/// <summary>Product upsert input, mirroring CreateTowarRequestDto.</summary>
public sealed record SferaProductInput(
    string Symbol,
    string Nazwa,
    string? Opis,
    decimal CenaEwidencyjna,
    string? WzorzecSymbol);

/// <summary>
/// Warehouse goods-receipt (przyjęcie magazynowe / przychód wewnętrzny — PW) input.
/// <see cref="DataPrzyjecia"/> is the entry/stock-movement date (computed by the adapter
/// off the domain clock).
/// </summary>
public sealed record SferaReceiptInput(
    string Symbol,
    decimal Ilosc,
    string Magazyn,
    string? Opis,
    string? NumerPartii,
    DateTime DataPrzyjecia);

/// <summary>
/// One correction line (original Lp -> new quantity and/or new GROSS unit price),
/// mirroring KorektaLineDto. At least one of <see cref="NowaIlosc"/> / <see cref="NowaCena"/>
/// is set (enforced by the API validator).
/// </summary>
public sealed record SferaCorrectionLineInput(int Lp, decimal? NowaIlosc, decimal? NowaCena);

/// <summary>Correction input against an original document.</summary>
public sealed record SferaCorrectionInput(
    string? Przyczyna,
    IReadOnlyList<SferaCorrectionLineInput> Lines);
