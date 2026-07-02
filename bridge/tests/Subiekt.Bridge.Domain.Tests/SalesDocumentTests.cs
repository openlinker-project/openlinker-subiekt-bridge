using Subiekt.Bridge.Domain.Common;
using Subiekt.Bridge.Domain.Invoices;
using Xunit;

namespace Subiekt.Bridge.Domain.Tests;

public class SalesDocumentTests
{
    private const string Pln = "PLN";

    private static VatRate Vat(string s) => VatRate.TryCreate(s).Value;

    private static Money Zl(decimal amount) => new(amount, Pln);

    private static InvoiceLine Line(string symbol, decimal qty, decimal price, string vat = "23")
        => new(symbol, qty, Zl(price), Vat(vat));

    private static SalesDocument Doc(params InvoiceLine[] lines)
        => SalesDocument.Create(DocumentType.FV, buyerId: 1, Pln, DateTimeOffset.Now, lines).Value;

    // --- issue #1: explicit payment selection on the aggregate ---

    [Fact]
    public void Create_WithoutPayment_HasNullPayment()
    {
        var doc = Doc(Line("A", 1, 10m));

        Assert.Null(doc.Payment);
    }

    [Fact]
    public void Create_FvWithTransferPayment_CarriesSelection()
    {
        var payment = PaymentSelection.TryCreate("transfer", 100007).Value;

        var result = SalesDocument.Create(
            DocumentType.FV, buyerId: 1, Pln, DateTimeOffset.Now,
            new[] { Line("A", 1, 10m) }, payment);

        Assert.True(result.IsSuccess);
        Assert.Equal(PaymentMethod.Transfer, result.Value.Payment!.Method);
        Assert.Equal(100007, result.Value.Payment.BankAccountId);
    }

    [Fact]
    public void Create_PaWithPayment_IsRejected()
    {
        var payment = PaymentSelection.TryCreate("cash", null).Value;

        var result = SalesDocument.Create(
            DocumentType.PA, buyerId: 1, Pln, DateTimeOffset.Now,
            new[] { Line("A", 1, 10m) }, payment);

        Assert.True(result.IsFailure);
        Assert.Equal("doc.payment.pa", result.Error.Code);
    }

    // --- issue #5: explicit Oddzial/Stanowisko Kasowe selection on the aggregate ---

    [Fact]
    public void Create_WithoutBranch_HasNullBranch()
    {
        var doc = Doc(Line("A", 1, 10m));

        Assert.Null(doc.Branch);
    }

    [Fact]
    public void Create_FvWithBranch_CarriesSelection()
    {
        var branch = BranchSelection.TryCreate(100001, 100066).Value;

        var result = SalesDocument.Create(
            DocumentType.FV, buyerId: 1, Pln, DateTimeOffset.Now,
            new[] { Line("A", 1, 10m) }, payment: null, branch: branch);

        Assert.True(result.IsSuccess);
        Assert.Equal(100001, result.Value.Branch!.OddzialId);
        Assert.Equal(100066, result.Value.Branch.StanowiskoKasoweId);
    }

    [Fact]
    public void Create_PaWithBranch_IsRejected()
    {
        var branch = BranchSelection.TryCreate(null, 100066).Value;

        var result = SalesDocument.Create(
            DocumentType.PA, buyerId: 1, Pln, DateTimeOffset.Now,
            new[] { Line("A", 1, 10m) }, payment: null, branch: branch);

        Assert.True(result.IsFailure);
        Assert.Equal("doc.branch.pa", result.Error.Code);
    }

    [Fact]
    public void FoldDiscounts_PreservesBranchSelection()
    {
        var branch = BranchSelection.TryCreate(100001, 100066).Value;
        var doc = SalesDocument.Create(
            DocumentType.FV, buyerId: 1, Pln, DateTimeOffset.Now,
            new[] { Line("A", 2, 100m), Line("DISC", 1, -30m) }, payment: null, branch: branch).Value;

        var folded = doc.FoldDiscounts();

        Assert.NotNull(folded.Branch);
        Assert.Equal(100001, folded.Branch!.OddzialId);
        Assert.Equal(100066, folded.Branch.StanowiskoKasoweId);
    }

    [Fact]
    public void FoldDiscounts_PreservesPaymentSelection()
    {
        var payment = PaymentSelection.TryCreate("transfer", 100007).Value;
        var doc = SalesDocument.Create(
            DocumentType.FV, buyerId: 1, Pln, DateTimeOffset.Now,
            new[] { Line("A", 2, 100m), Line("DISC", 1, -30m) }, payment).Value;

        var folded = doc.FoldDiscounts();

        Assert.NotNull(folded.Payment);
        Assert.Equal(PaymentMethod.Transfer, folded.Payment!.Method);
        Assert.Equal(100007, folded.Payment.BankAccountId);
    }

    [Fact]
    public void FoldDiscounts_WorkedExample_FoldsProportionallyAndDropsDiscountLine()
    {
        // A: 100 x 2 = 200 ; B: 50 x 1 = 50 ; discount: -30 x 1
        // posTotal = 250, discTotal = 30, factor = 220/250 = 0.88
        // effA = 88.00 ; effB = 44.00
        var doc = Doc(
            Line("A", 2, 100m),
            Line("B", 1, 50m),
            Line("DISC", 1, -30m));

        var folded = doc.FoldDiscounts();

        Assert.Equal(2, folded.Lines.Count); // discount line dropped
        Assert.DoesNotContain(folded.Lines, l => l.IsDiscount);
        Assert.Equal(88.00m, folded.Lines[0].UnitGrossPrice.Amount);
        Assert.Equal(44.00m, folded.Lines[1].UnitGrossPrice.Amount);
        // Quantities and VAT untouched.
        Assert.Equal(2m, folded.Lines[0].Quantity);
        Assert.Equal("23", folded.Lines[0].VatRate.Symbol);
    }

    [Fact]
    public void FoldDiscounts_RoundsAwayFromZeroToTwoPlaces()
    {
        // price 10 x 3 = 30 ; discount -10 → factor = 20/30 = 0.6666...
        // eff = round(10 * 0.66666..., 2, AwayFromZero) = 6.67
        var doc = Doc(
            Line("A", 3, 10m),
            Line("DISC", 1, -10m));

        var folded = doc.FoldDiscounts();

        Assert.Single(folded.Lines);
        Assert.Equal(6.67m, folded.Lines[0].UnitGrossPrice.Amount);
    }

    [Fact]
    public void FoldDiscounts_NoDiscount_LeavesPricesUnchanged()
    {
        var doc = Doc(Line("A", 2, 100m), Line("B", 1, 50m));

        var folded = doc.FoldDiscounts();

        Assert.Equal(2, folded.Lines.Count);
        Assert.Equal(100m, folded.Lines[0].UnitGrossPrice.Amount);
        Assert.Equal(50m, folded.Lines[1].UnitGrossPrice.Amount);
    }

    [Fact]
    public void FoldDiscounts_DiscountAtLeastPositiveTotal_FactorIsOne_DropsDiscountOnly()
    {
        // discount (100) >= positive total (100) → guard fails → factor 1, prices kept,
        // discount line still dropped (no negative position allowed).
        var doc = Doc(Line("A", 1, 100m), Line("DISC", 1, -100m));

        var folded = doc.FoldDiscounts();

        Assert.Single(folded.Lines);
        Assert.Equal(100m, folded.Lines[0].UnitGrossPrice.Amount);
    }

    [Fact]
    public void FoldDiscounts_NonPositiveQuantityCountsAsOne()
    {
        // A: price 100, qty 0 → effective qty 1 → posTotal 100 ; discount -20
        // factor = 80/100 = 0.8 → eff = 80.00
        var doc = Doc(Line("A", 0, 100m), Line("DISC", 1, -20m));

        var folded = doc.FoldDiscounts();

        Assert.Single(folded.Lines);
        Assert.Equal(80.00m, folded.Lines[0].UnitGrossPrice.Amount);
    }

    [Fact]
    public void ComputeVatSplits_SplitsGrossIntoNetAndVat_PerRate()
    {
        // 123.00 brutto at 23% → net 100.00, vat 23.00
        var doc = Doc(Line("A", 1, 123m, "23"));

        var splits = doc.ComputeVatSplits();

        var split = Assert.Single(splits);
        Assert.Equal("23", split.Rate.Symbol);
        Assert.Equal(123.00m, split.Gross.Amount);
        Assert.Equal(100.00m, split.Net.Amount);
        Assert.Equal(23.00m, split.Vat.Amount);
    }

    [Fact]
    public void ComputeVatSplits_ExemptRate_HasZeroVat()
    {
        var doc = Doc(Line("A", 1, 100m, "zw"));

        var split = Assert.Single(doc.ComputeVatSplits());

        Assert.Equal("zw", split.Rate.Symbol);
        Assert.Equal(100m, split.Net.Amount);
        Assert.Equal(0m, split.Vat.Amount);
    }

    [Fact]
    public void ComputeVatSplits_LiveOracle_FS147_MatchesSferaPrzelicz()
    {
        // Reproduces a real invoice issued through Sfera (document FS 147):
        //   Line A: unitGross 90.00, qty 1, rate "23" => net 73.17, vat 16.83, gross 90.00
        //   Line B: unitGross 45.00, qty 2, rate "8"  => net 83.34, vat 6.67,  gross 90.01
        // Subiekt rounds the net per unit, then derives VAT from the rounded line net.
        var doc = Doc(
            Line("A", 1, 90.00m, "23"),
            Line("B", 2, 45.00m, "8"));

        var splits = doc.ComputeVatSplits();

        var s23 = Assert.Single(splits, x => x.Rate.Symbol == "23");
        Assert.Equal(73.17m, s23.Net.Amount);
        Assert.Equal(16.83m, s23.Vat.Amount);
        Assert.Equal(90.00m, s23.Gross.Amount);

        var s8 = Assert.Single(splits, x => x.Rate.Symbol == "8");
        Assert.Equal(83.34m, s8.Net.Amount);
        Assert.Equal(6.67m, s8.Vat.Amount);
        Assert.Equal(90.01m, s8.Gross.Amount);

        // Document totals: net 156.51, vat 23.50, gross 180.01.
        Assert.Equal(156.51m, splits.Sum(x => x.Net.Amount));
        Assert.Equal(23.50m, splits.Sum(x => x.Vat.Amount));
        Assert.Equal(180.01m, splits.Sum(x => x.Gross.Amount));
    }

    [Fact]
    public void Create_NoLines_Fails()
    {
        var result = SalesDocument.Create(
            DocumentType.FV, 1, Pln, DateTimeOffset.Now, Array.Empty<InvoiceLine>());

        Assert.True(result.IsFailure);
        Assert.Equal("doc.lines", result.Error.Code);
    }
}
