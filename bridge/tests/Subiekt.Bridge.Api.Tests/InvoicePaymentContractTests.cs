using SferaApi.Contracts;
using SferaApi.Models;
using SferaApi.Validation;
using Subiekt.Bridge.Domain.Invoices;
using Xunit;

namespace Subiekt.Bridge.Api.Tests;

// Issue #1: wire-level coverage of the payment-selection fields — the FluentValidation
// SHAPE rules (400 path) and the mapper threading into the Domain aggregate (422 path).
// No Sfera/InsERT type is touched.
public class InvoicePaymentContractTests
{
    private static CreateInvoiceRequestDto Request(
        string? paymentMethod = null,
        int? bankAccountId = null,
        string documentType = "FV")
        => new()
        {
            KontrahentId = 1,
            DocumentType = documentType,
            PaymentMethod = paymentMethod,
            BankAccountId = bankAccountId,
            Lines = { new CreateInvoiceLineRequestDto { TowarSymbol = "A", Ilosc = 1, CenaBrutto = 10m, StawkaVAT = "23" } },
        };

    // --- validator (shape) ---

    [Theory]
    [InlineData(null)]
    [InlineData("cash")]
    [InlineData("Transfer")]
    public void Validator_AcceptsKnownPaymentMethods(string? method)
    {
        var result = InvoiceValidators.Create.Validate(Request(method, method?.ToLowerInvariant() == "transfer" ? 5 : null));

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void Validator_RejectsUnknownPaymentMethod()
    {
        var result = InvoiceValidators.Create.Validate(Request("card"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("PaymentMethod"));
    }

    [Fact]
    public void Validator_RejectsNonPositiveBankAccountId()
    {
        var result = InvoiceValidators.Create.Validate(Request("transfer", 0));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("BankAccountId"));
    }

    // --- mapper (Domain threading) ---

    [Fact]
    public void Build_WithoutPaymentFields_ProducesDocumentWithNullPayment()
    {
        var built = InvoiceContractMapper.Build(Request(), "PLN");

        Assert.True(built.IsSuccess);
        Assert.Null(built.Value.Document.Payment);
    }

    [Fact]
    public void Build_TransferWithAccount_ProducesDocumentWithSelection()
    {
        var built = InvoiceContractMapper.Build(Request("transfer", 100007), "PLN");

        Assert.True(built.IsSuccess);
        Assert.Equal(PaymentMethod.Transfer, built.Value.Document.Payment!.Method);
        Assert.Equal(100007, built.Value.Document.Payment.BankAccountId);
    }

    [Fact]
    public void Build_TransferWithoutAccount_FailsWithDomainError()
    {
        var built = InvoiceContractMapper.Build(Request("transfer"), "PLN");

        Assert.True(built.IsFailure);
        Assert.Equal("payment.bankAccount", built.Error.Code);
    }

    [Fact]
    public void Build_CashWithAccount_FailsWithDomainError()
    {
        var built = InvoiceContractMapper.Build(Request("cash", 100007), "PLN");

        Assert.True(built.IsFailure);
        Assert.Equal("payment.bankAccount", built.Error.Code);
    }

    [Fact]
    public void Build_BankAccountWithoutMethod_FailsWithDomainError()
    {
        var built = InvoiceContractMapper.Build(Request(null, 100007), "PLN");

        Assert.True(built.IsFailure);
        Assert.Equal("payment.bankAccount", built.Error.Code);
    }

    [Fact]
    public void Build_PaWithPayment_FailsWithDomainError()
    {
        var built = InvoiceContractMapper.Build(Request("cash", null, documentType: "PA"), "PLN");

        Assert.True(built.IsFailure);
        Assert.Equal("doc.payment.pa", built.Error.Code);
    }
}
