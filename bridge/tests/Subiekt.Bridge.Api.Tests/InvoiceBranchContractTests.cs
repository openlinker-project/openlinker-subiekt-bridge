using SferaApi.Contracts;
using SferaApi.Models;
using SferaApi.Validation;
using Xunit;

namespace Subiekt.Bridge.Api.Tests;

// Issue #5: wire-level coverage of the Oddzial/Stanowisko Kasowe selection fields — the
// FluentValidation SHAPE rules (400 path) and the mapper threading into the Domain
// aggregate (422 path). No Sfera/InsERT type is touched. Mirrors InvoicePaymentContractTests.
public class InvoiceBranchContractTests
{
    private static CreateInvoiceRequestDto Request(
        int? oddzialId = null,
        int? stanowiskoKasoweId = null,
        string documentType = "FV")
        => new()
        {
            KontrahentId = 1,
            DocumentType = documentType,
            OddzialId = oddzialId,
            StanowiskoKasoweId = stanowiskoKasoweId,
            Lines = { new CreateInvoiceLineRequestDto { TowarSymbol = "A", Ilosc = 1, CenaBrutto = 10m, StawkaVAT = "23" } },
        };

    // --- validator (shape) ---

    [Fact]
    public void Validator_AcceptsBothAbsent()
    {
        var result = InvoiceValidators.Create.Validate(Request());

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void Validator_AcceptsBothPositive()
    {
        var result = InvoiceValidators.Create.Validate(Request(100001, 100066));

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void Validator_RejectsNonPositiveOddzialId()
    {
        var result = InvoiceValidators.Create.Validate(Request(0, 100066));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("OddzialId"));
    }

    [Fact]
    public void Validator_RejectsNonPositiveStanowiskoKasoweId()
    {
        var result = InvoiceValidators.Create.Validate(Request(100001, 0));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("StanowiskoKasoweId"));
    }

    // --- mapper (Domain threading) ---

    [Fact]
    public void Build_WithoutBranchFields_ProducesDocumentWithNullBranch()
    {
        var built = InvoiceContractMapper.Build(Request(), "PLN");

        Assert.True(built.IsSuccess);
        Assert.Null(built.Value.Document.Branch);
    }

    [Fact]
    public void Build_StanowiskoAlone_ProducesDocumentWithSelection()
    {
        var built = InvoiceContractMapper.Build(Request(stanowiskoKasoweId: 100066), "PLN");

        Assert.True(built.IsSuccess);
        Assert.Null(built.Value.Document.Branch!.OddzialId);
        Assert.Equal(100066, built.Value.Document.Branch.StanowiskoKasoweId);
    }

    [Fact]
    public void Build_BothPresent_ProducesDocumentWithSelection()
    {
        var built = InvoiceContractMapper.Build(Request(100001, 100066), "PLN");

        Assert.True(built.IsSuccess);
        Assert.Equal(100001, built.Value.Document.Branch!.OddzialId);
        Assert.Equal(100066, built.Value.Document.Branch.StanowiskoKasoweId);
    }

    [Fact]
    public void Build_OddzialWithoutStanowisko_FailsWithDomainError()
    {
        var built = InvoiceContractMapper.Build(Request(oddzialId: 100001), "PLN");

        Assert.True(built.IsFailure);
        Assert.Equal("branch.stanowisko", built.Error.Code);
    }

    [Fact]
    public void Build_PaWithBranch_FailsWithDomainError()
    {
        var built = InvoiceContractMapper.Build(Request(stanowiskoKasoweId: 100066, documentType: "PA"), "PLN");

        Assert.True(built.IsFailure);
        Assert.Equal("doc.branch.pa", built.Error.Code);
    }
}
