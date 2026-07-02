using FluentValidation;
using SferaApi.Models;

namespace SferaApi.Validation;

// FluentValidation validators for the WRITE requests. These check shape only:
// required fields, lengths, non-empty collections, positive quantities, currency.
//
// IMPORTANT: the NIP CHECKSUM is NOT validated here — that stays in the Domain
// `Nip` value object (Nip.TryCreate), invoked by the handlers/mappers. The
// validators only assert the NIP is structurally plausible when present (digits
// after stripping separators, plausible length) so we fail fast on obvious junk
// without duplicating the canonical checksum rule.
public static class NipPlausibility
{
    // A present NIP must reduce to 10 digits once common separators are stripped.
    // Empty/absent NIP is allowed (optional). The Domain enforces the checksum.
    public static bool IsPlausible(string? nip)
    {
        if (string.IsNullOrWhiteSpace(nip)) return true; // optional
        var digits = new string(nip.Where(char.IsDigit).ToArray());
        return digits.Length == 10;
    }
}

public sealed class CreateFirmaRequestValidator : AbstractValidator<CreateFirmaRequestDto>
{
    public CreateFirmaRequestValidator()
    {
        RuleFor(x => x.NazwaSkrocona)
            .NotEmpty().WithMessage("NazwaSkrocona jest wymagana.")
            .MaximumLength(255).WithMessage("NazwaSkrocona jest za długa (max 255).");

        RuleFor(x => x.NIP)
            .Must(NipPlausibility.IsPlausible)
            .WithMessage("NIP musi zawierać 10 cyfr (po usunięciu separatorów).");

        RuleFor(x => x.Telefon)
            .MaximumLength(64).WithMessage("Telefon jest za długi (max 64).");

        RuleFor(x => x.Email)
            .MaximumLength(255).WithMessage("Email jest za długi (max 255).");

        RuleFor(x => x.Typ)
            .Must(t => string.IsNullOrWhiteSpace(t)
                || string.Equals(t, "firma", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "osoba", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Typ musi być 'firma' lub 'osoba'.");
    }
}

public sealed class CreateTowarRequestValidator : AbstractValidator<CreateTowarRequestDto>
{
    public CreateTowarRequestValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol jest wymagany.")
            .MaximumLength(50).WithMessage("Symbol jest za długi (max 50).");

        RuleFor(x => x.Nazwa)
            .NotEmpty().WithMessage("Nazwa jest wymagana.")
            .MaximumLength(255).WithMessage("Nazwa jest za długa (max 255).");

        RuleFor(x => x.CenaEwidencyjna)
            .GreaterThanOrEqualTo(0m).WithMessage("CenaEwidencyjna nie może być ujemna.");

        RuleFor(x => x.WzorzecSymbol)
            .MaximumLength(50).WithMessage("WzorzecSymbol jest za długi (max 50).");
    }
}

public sealed class CreateInvoiceLineValidator : AbstractValidator<CreateInvoiceLineRequestDto>
{
    public CreateInvoiceLineValidator()
    {
        // A line must reference a catalogue symbol OR carry a display name (the
        // bridge then adds a one-time line). Discount lines (negative gross) are
        // allowed, so quantity must be non-zero but may pair with any sign of price.
        RuleFor(x => x)
            .Must(l => !string.IsNullOrWhiteSpace(l.TowarSymbol) || !string.IsNullOrWhiteSpace(l.Name))
            .WithMessage("Każda pozycja musi mieć TowarSymbol lub Name.");

        RuleFor(x => x.Ilosc)
            .NotEqual(0m).WithMessage("Ilosc pozycji nie może być zerowa.");

        RuleFor(x => x.StawkaVAT)
            .NotEmpty().WithMessage("StawkaVAT jest wymagana.");
    }
}

public sealed class CreateInvoiceRequestValidator : AbstractValidator<CreateInvoiceRequestDto>
{
    public CreateInvoiceRequestValidator()
    {
        RuleFor(x => x.Lines)
            .NotEmpty().WithMessage("Faktura musi mieć co najmniej jedną pozycję.");

        RuleForEach(x => x.Lines).SetValidator(new CreateInvoiceLineValidator());

        RuleFor(x => x.Currency)
            .Must(c => string.IsNullOrWhiteSpace(c) || c.Trim().Length == 3)
            .WithMessage("Currency musi być 3-literowym kodem ISO (np. PLN).");

        RuleFor(x => x.DocumentType)
            .Must(t => string.IsNullOrWhiteSpace(t)
                || string.Equals(t, "FV", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "PA", StringComparison.OrdinalIgnoreCase))
            .WithMessage("DocumentType musi być 'FV' lub 'PA'.");

        // Issue #1 payment fields: SHAPE only (vocabulary + positivity). The strict
        // combination rules (transfer requires account, cash forbids it, account
        // alone rejected, PA unsupported) are the Domain PaymentSelection /
        // SalesDocument rules and surface as 422 via the build-failure path.
        RuleFor(x => x.PaymentMethod)
            .Must(m => string.IsNullOrWhiteSpace(m)
                || string.Equals(m.Trim(), "cash", StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.Trim(), "transfer", StringComparison.OrdinalIgnoreCase))
            .WithMessage("PaymentMethod musi być 'cash' lub 'transfer'.");

        RuleFor(x => x.BankAccountId)
            .Must(id => !id.HasValue || id.Value > 0)
            .WithMessage("BankAccountId musi być dodatnie.");

        // Issue #5 branch fields: SHAPE only (positivity). The strict combination rule
        // (OddzialId requires StanowiskoKasoweId; cross-consistency check) is the Domain
        // BranchSelection rule and surfaces as 422 via the build-failure path.
        RuleFor(x => x.OddzialId)
            .Must(id => !id.HasValue || id.Value > 0)
            .WithMessage("OddzialId musi być dodatnie.");

        RuleFor(x => x.StanowiskoKasoweId)
            .Must(id => !id.HasValue || id.Value > 0)
            .WithMessage("StanowiskoKasoweId musi być dodatnie.");

        // Either an explicit KontrahentId, or an inline buyer with a name, must be
        // present so the invoice has a payer.
        RuleFor(x => x)
            .Must(r => r.KontrahentId > 0 || (r.Buyer != null && !string.IsNullOrWhiteSpace(r.Buyer.Name)))
            .WithMessage("Wymagany KontrahentId > 0 albo Buyer.Name.");

        // When an inline buyer NIP is provided it must be structurally plausible
        // (checksum stays in the Domain).
        When(x => x.Buyer != null, () =>
        {
            RuleFor(x => x.Buyer!.Nip)
                .Must(NipPlausibility.IsPlausible)
                .WithMessage("Buyer.Nip musi zawierać 10 cyfr (po usunięciu separatorów).");
            RuleFor(x => x.Buyer!.Name)
                .MaximumLength(255).WithMessage("Buyer.Name jest za długi (max 255).");
        });
    }
}

public sealed class KorektaRequestValidator : AbstractValidator<KorektaRequestDto>
{
    public KorektaRequestValidator()
    {
        RuleFor(x => x.Lines)
            .NotEmpty().WithMessage("Korekta musi mieć co najmniej jedną pozycję.");

        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.Lp)
                .GreaterThan(0).WithMessage("Lp pozycji korekty musi być > 0.");

            // A correction line must change at least one of quantity or price.
            line.RuleFor(l => l)
                .Must(l => l.NowaIlosc.HasValue || l.NowaCena.HasValue)
                .WithMessage("Pozycja korekty musi mieć NowaIlosc lub NowaCena.");

            line.RuleFor(l => l.NowaIlosc)
                .GreaterThanOrEqualTo(0m).When(l => l.NowaIlosc.HasValue)
                .WithMessage("NowaIlosc nie może być ujemna.");

            line.RuleFor(l => l.NowaCena)
                .GreaterThanOrEqualTo(0m).When(l => l.NowaCena.HasValue)
                .WithMessage("NowaCena nie może być ujemna.");
        });
    }
}
