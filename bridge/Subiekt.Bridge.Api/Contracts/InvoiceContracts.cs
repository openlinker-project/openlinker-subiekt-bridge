using SferaApi.Models;
using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;
using Subiekt.Bridge.Domain.Customers;
using Subiekt.Bridge.Domain.Invoices;

namespace SferaApi.Contracts;

/// <summary>
/// Maps the legacy <see cref="CreateInvoiceRequestDto"/> wire shape onto the Domain
/// <see cref="SalesDocument"/> (+ an optional inline buyer) the Application ports
/// consume. Keeps the wire contract unchanged for the existing cockpit/OL.
/// </summary>
public static class InvoiceContractMapper
{
    /// <summary>
    /// Build the Domain document and, when the request carries an inline buyer with no
    /// explicit <c>KontrahentId</c>, an <see cref="InlineBuyer"/> to upsert-then-bill in
    /// one unit of work. Validation failures (bad VAT/NIP, empty lines, ...) come back as
    /// a failed <see cref="Result"/> carrying a safe domain message.
    /// </summary>
    public static Result<(SalesDocument Document, InlineBuyer? Buyer)> Build(CreateInvoiceRequestDto req, string currency)
    {
        var documentType = string.Equals(req.DocumentType, "PA", StringComparison.OrdinalIgnoreCase)
            ? DocumentType.PA
            : DocumentType.FV;

        // Self-sufficient mode: OL sends the buyer INLINE (no customerId). When no
        // KontrahentId was supplied and a buyer name is present, build an inline buyer to
        // upsert in the same write unit; otherwise bill the explicit KontrahentId.
        InlineBuyer? inlineBuyer = null;
        var hasInlineBuyer = req.KontrahentId <= 0 && req.Buyer != null && !string.IsNullOrWhiteSpace(req.Buyer.Name);
        if (hasInlineBuyer)
        {
            var buyerResult = BuildBuyer(req.Buyer!);
            if (buyerResult.IsFailure)
                return Result.Failure<(SalesDocument, InlineBuyer?)>(buyerResult.Error);
            inlineBuyer = new InlineBuyer(buyerResult.Value);
        }

        // SalesDocument.Create requires a positive buyer id. When the buyer is inline the
        // real provider id is only known after the upsert runs on the worker, so we pass a
        // sentinel (1) here — SferaIssueInvoiceWithBuyer OVERRIDES KontrahentId with the
        // upsert result before issuing, so the document's BuyerId is unused in that path.
        var buyerId = hasInlineBuyer ? 1 : req.KontrahentId;

        // Build the lines. A line references a catalogue product by symbol, or carries a
        // one-time name (product not in Subiekt). Discount lines (negative gross) are kept
        // here and folded by the Domain rule in the adapter.
        var lines = new List<InvoiceLine>(req.Lines.Count);
        foreach (var l in req.Lines)
        {
            var vatResult = VatRate.TryCreate(l.StawkaVAT);
            if (vatResult.IsFailure)
                return Result.Failure<(SalesDocument, InlineBuyer?)>(vatResult.Error);

            var hasSymbol = !string.IsNullOrWhiteSpace(l.TowarSymbol);
            // When there's no catalogue symbol, the display name becomes the one-time name.
            // A discount line (negative price) may carry neither a symbol nor a name; the
            // Domain rule folds it away by price sign, but InvoiceLine still needs a non-empty
            // symbol-or-name to construct — give it a synthetic label so we never throw.
            var oneTimeName = hasSymbol
                ? l.Name
                : (!string.IsNullOrWhiteSpace(l.Name) ? l.Name
                    : (l.CenaBrutto < 0m ? "Rabat" : "Pozycja"));

            lines.Add(new InvoiceLine(
                productSymbol: l.TowarSymbol,
                quantity: l.Ilosc,
                unitGrossPrice: new Money(l.CenaBrutto, currency),
                vatRate: vatResult.Value,
                oneTimeName: oneTimeName));
        }

        var issueDate = new DateTimeOffset(req.IssueDate ?? DateTime.Now);

        // Issue #1: explicit payment selection — strict combination rules live in
        // the Domain value object; a failure surfaces as the standard 422 build path.
        var paymentResult = PaymentSelection.TryCreate(req.PaymentMethod, req.BankAccountId);
        if (paymentResult.IsFailure)
            return Result.Failure<(SalesDocument, InlineBuyer?)>(paymentResult.Error);

        var docResult = SalesDocument.Create(documentType, buyerId, currency, issueDate, lines, paymentResult.Value);
        if (docResult.IsFailure)
            return Result.Failure<(SalesDocument, InlineBuyer?)>(docResult.Error);

        return Result.Success((docResult.Value, inlineBuyer));
    }

    private static Result<Customer> BuildBuyer(BuyerDto buyer)
    {
        // A buyer with a NIP, or explicitly flagged IsCompany, is a firma. (Mirrors the
        // legacy: isCompany = Buyer.IsCompany || NIP present.)
        var isCompany = buyer.IsCompany || !string.IsNullOrWhiteSpace(buyer.Nip);

        Nip? nip = null;
        if (!string.IsNullOrWhiteSpace(buyer.Nip))
        {
            var nipResult = Nip.TryCreate(buyer.Nip);
            if (nipResult.IsFailure)
                return Result.Failure<Customer>(nipResult.Error);
            nip = nipResult.Value;
        }

        Address? address = buyer.Address is null
            ? null
            : new Address(
                buyer.Address.Ulica,
                buyer.Address.NrDomu,
                buyer.Address.NrLokalu,
                buyer.Address.KodPocztowy,
                buyer.Address.Miejscowosc,
                buyer.Address.Poczta,
                string.IsNullOrWhiteSpace(buyer.Address.CountryCode) ? "PL" : buyer.Address.CountryCode);

        return Customer.Create(buyer.Name, nip, isCompany, buyer.Telefon, address);
    }
}
