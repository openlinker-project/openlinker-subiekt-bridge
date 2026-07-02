namespace SferaApi.Models;

public class ResponseEnvelope<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }

    // Structured error matching the OL contract's two classes.
    // null on success. See BridgeError below.
    public BridgeError? Error { get; set; }
}

public class BridgeError
{
    // "unreachable" | "rejected" | "unauthorized" | "bad_request" | "not_implemented" | "internal"
    public string Code { get; set; } = "";
    public string Reason { get; set; } = "";

    // Correlation id tying a generic client error back to the full server-side
    // log entry. null when not applicable. The detailed message stays in the log.
    public string? CorrelationId { get; set; }
}

public class KontrahentRequest
{
    public string NazwaSkrocona { get; set; } = "";
    public string? NIP { get; set; }
    public string? Telefon { get; set; }
    public bool Aktywny { get; set; } = true;
    public string Typ { get; set; } = "firma"; // "firma" | "osoba"
}

public class FakturaRequest
{
    public int KontrahentId { get; set; }
    public List<FakturaLineItem> Towary { get; set; } = [];
    public string DokTyp { get; set; } = "faktura"; // "faktura" | "paragon"
    public DateTime DataSprzedazy { get; set; } = DateTime.Now;
}

public class FakturaLineItem
{
    public string Symbol { get; set; } = "";
    public decimal Ilosc { get; set; }
    public decimal Cena { get; set; }
}

// Enhanced DTOs for write operations
public class CreateFirmaRequestDto
{
    public string NazwaSkrocona { get; set; } = "";
    public string? NIP { get; set; }
    public string? Telefon { get; set; }
    public string? Email { get; set; }
    public string? Adres { get; set; }   // legacy free-text (kept for the cockpit)
    public bool Aktywny { get; set; } = true;
    public string Typ { get; set; } = "firma"; // firma | osoba

    // Structured address (OL contract Address). When provided it maps 1:1 onto
    // Subiekt's AdresPodstawowy (Ulica/NrDomu/KodPocztowy/Miejscowosc).
    public AddressDto? Address { get; set; }
}

public class AddressDto
{
    public string? Ulica { get; set; }        // line1 (street + number) or street
    public string? NrDomu { get; set; }
    public string? NrLokalu { get; set; }
    public string? KodPocztowy { get; set; }  // postalCode
    public string? Miejscowosc { get; set; }  // city
    public string? Poczta { get; set; }
    public string CountryCode { get; set; } = "PL";
}

public class CreateInvoiceRequestDto
{
    public int KontrahentId { get; set; }
    public string DocumentType { get; set; } = "FV";
    public string Currency { get; set; } = "PLN";
    public DateTime? IssueDate { get; set; }
    public List<CreateInvoiceLineRequestDto> Lines { get; set; } = [];

    // OL-contract correlation/idempotency fields (additive; optional).
    // orderId is echoed back; idempotencyKey makes a retried call return the
    // SAME invoice instead of issuing a duplicate (see IIdempotencyStore).
    public string? OrderId { get; set; }
    public string? IdempotencyKey { get; set; }

    // OL contract carries the buyer INLINE (no customerId). When KontrahentId is
    // not supplied, the bridge auto-upserts this buyer and uses the result — so
    // issueInvoice is self-sufficient (one call). Two-step (explicit KontrahentId)
    // still works and takes precedence.
    public BuyerDto? Buyer { get; set; }

    // Issue #1: EXPLICIT payment selection (additive; optional). Strict semantics
    // (enforced by the Domain PaymentSelection rule): "transfer" REQUIRES
    // BankAccountId; "cash" must NOT carry one; BankAccountId alone is rejected;
    // both absent = today's provider-default payments (no regression). Not
    // supported for DocumentType "PA".
    public string? PaymentMethod { get; set; }   // "cash" | "transfer"

    // Subiekt bank-account id, as returned by GET /api/bank-accounts.
    public int? BankAccountId { get; set; }

    // Issue #5: EXPLICIT branch/cash-register selection (additive; optional). Strict
    // semantics (enforced by the Domain BranchSelection rule): OddzialId REQUIRES an
    // explicit StanowiskoKasoweId (Sfera's implicit default cash-register does not scope
    // to a non-default branch); StanowiskoKasoweId alone is allowed (keeps the document's
    // default branch); both absent = today's implicit-default behavior (no regression).
    // Not supported for DocumentType "PA". The station must be linked to the given Oddzial
    // (or, if OddzialId is absent, left unlinked) - see GET /api/cash-registers.
    public int? OddzialId { get; set; }
    public int? StanowiskoKasoweId { get; set; }
}

public class BuyerDto
{
    public string Name { get; set; } = "";
    public string? Nip { get; set; }
    public bool IsCompany { get; set; } = true;
    public string? Telefon { get; set; }
    public AddressDto? Address { get; set; }
}

// Correction (faktura korygująca) to an existing invoice — adjusts line
// quantities to a new value (e.g. partial return).
public class KorektaRequestDto
{
    public string? Przyczyna { get; set; }
    public List<KorektaLineDto> Lines { get; set; } = [];

    // OL-contract idempotency field (additive; optional). When set, a retried
    // correction call returns the SAME faktura korygująca instead of issuing a
    // duplicate (permanent) fiscal document. See IIdempotencyStore.
    public string? IdempotencyKey { get; set; }
}
public class KorektaLineDto
{
    public int Lp { get; set; }             // line number (Lp) on the original document
    public decimal? NowaIlosc { get; set; } // corrected quantity (e.g. 1 of original 3), optional
    public decimal? NowaCena { get; set; }  // corrected GROSS unit price, optional
}

// Create a catalogue product (towar) — simulates PrestaShop → Subiekt sync.
public class CreateTowarRequestDto
{
    public string Symbol { get; set; } = "";
    public string Nazwa { get; set; } = "";
    public string? Opis { get; set; }
    public decimal CenaEwidencyjna { get; set; }
    // Optional: symbol of an existing product to copy VAT/unit/rodzaj from.
    public string? WzorzecSymbol { get; set; }
}

public class CreateInvoiceLineRequestDto
{
    public string TowarSymbol { get; set; } = "";
    public decimal Ilosc { get; set; }
    public decimal CenaBrutto { get; set; }
    public string StawkaVAT { get; set; } = "23";

    // Display name (OL contract: lines[].name). Used when TowarSymbol is NOT in
    // Subiekt's catalogue — the bridge then adds a one-time service line under
    // this name (PrestaShop product not synced to Subiekt). Falls back to symbol.
    public string? Name { get; set; }
}
