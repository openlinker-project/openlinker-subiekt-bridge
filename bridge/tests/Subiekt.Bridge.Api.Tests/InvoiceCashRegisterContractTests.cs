using SferaApi.Contracts;
using SferaApi.Models;
using SferaApi.Validation;
using Xunit;

namespace Subiekt.Bridge.Api.Tests;

// Issue #5: wire-level coverage of the Stanowisko Kasowe selection field - the
// FluentValidation SHAPE rule (400 path) and the mapper threading into the Domain
// aggregate (422 path). No Sfera/InsERT type is touched. Mirrors InvoicePaymentContractTests.
public class InvoiceCashRegisterContractTests
{
    private static CreateInvoiceRequestDto Request(
        int? stanowiskoKasoweId = null,
        string documentType = "FV")
        => new()
        {
            KontrahentId = 1,
            DocumentType = documentType,
            StanowiskoKasoweId = stanowiskoKasoweId,
            Lines = { new CreateInvoiceLineRequestDto { TowarSymbol = "A", Ilosc = 1, CenaBrutto = 10m, StawkaVAT = "23" } },
        };

    // --- validator (shape) ---

    [Fact]
    public void Validator_AcceptsAbsent()
    {
        var result = InvoiceValidators.Create.Validate(Request());

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void Validator_AcceptsPositive()
    {
        var result = InvoiceValidators.Create.Validate(Request(100066));

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void Validator_RejectsNonPositiveStanowiskoKasoweId()
    {
        var result = InvoiceValidators.Create.Validate(Request(0));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("StanowiskoKasoweId"));
    }

    // --- mapper (Domain threading) ---

    [Fact]
    public void Build_WithoutField_ProducesDocumentWithNullCashRegister()
    {
        var built = InvoiceContractMapper.Build(Request(), "PLN");

        Assert.True(built.IsSuccess);
        Assert.Null(built.Value.Document.CashRegister);
    }

    [Fact]
    public void Build_WithField_ProducesDocumentWithSelection()
    {
        var built = InvoiceContractMapper.Build(Request(stanowiskoKasoweId: 100066), "PLN");

        Assert.True(built.IsSuccess);
        Assert.Equal(100066, built.Value.Document.CashRegister!.StanowiskoKasoweId);
    }

    [Fact]
    public void Build_PaWithCashRegister_FailsWithDomainError()
    {
        var built = InvoiceContractMapper.Build(Request(stanowiskoKasoweId: 100066, documentType: "PA"), "PLN");

        Assert.True(built.IsFailure);
        Assert.Equal("doc.cashRegister.pa", built.Error.Code);
    }
}
