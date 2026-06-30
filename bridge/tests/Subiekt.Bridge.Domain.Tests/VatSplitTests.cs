using Subiekt.Bridge.Domain.Common;
using Subiekt.Bridge.Domain.Invoices;
using Xunit;

namespace Subiekt.Bridge.Domain.Tests;

public class VatSplitTests
{
    private const string Pln = "PLN";

    private static VatRate Vat(string s) => VatRate.TryCreate(s).Value;

    private static Money Zl(decimal amount) => new(amount, Pln);

    [Fact]
    public void FromGross_SplitsAggregateGrossIntoNetAndVat()
    {
        // FromGross behaviour is unchanged: net = round(gross / 1.23), vat = gross - net.
        var split = VatSplit.FromGross(Zl(123.00m), Vat("23"));

        Assert.Equal(123.00m, split.Gross.Amount);
        Assert.Equal(100.00m, split.Net.Amount);
        Assert.Equal(23.00m, split.Vat.Amount);
    }

    [Fact]
    public void FromGross_ExemptRate_HasZeroVat()
    {
        var split = VatSplit.FromGross(Zl(100.00m), Vat("zw"));

        Assert.Equal(100.00m, split.Net.Amount);
        Assert.Equal(0m, split.Vat.Amount);
        Assert.Equal(100.00m, split.Gross.Amount);
    }

    [Fact]
    public void ForLine_RoundsNetPerUnit_ThenDerivesVat_LineA()
    {
        // Live oracle FS 147 line A: unitGross 90.00, qty 1, rate 23%.
        // netUnit = round(90/1.23) = 73.17 ; vat = round(73.17*0.23) = 16.83 ; gross = 90.00.
        var split = VatSplit.ForLine(Zl(90.00m), 1m, Vat("23"));

        Assert.Equal(73.17m, split.Net.Amount);
        Assert.Equal(16.83m, split.Vat.Amount);
        Assert.Equal(90.00m, split.Gross.Amount);
    }

    [Fact]
    public void ForLine_PerUnitRounding_CanDivergeFromAggregateGross_LineB()
    {
        // Live oracle FS 147 line B: unitGross 45.00, qty 2, rate 8%.
        // netUnit = round(45/1.08) = 41.67 ; lineNet = 83.34 ; vat = round(83.34*0.08) = 6.67.
        // gross = 90.01, a grosz above unitGross*qty (90.00) — exactly what Sfera reports.
        var split = VatSplit.ForLine(Zl(45.00m), 2m, Vat("8"));

        Assert.Equal(83.34m, split.Net.Amount);
        Assert.Equal(6.67m, split.Vat.Amount);
        Assert.Equal(90.01m, split.Gross.Amount);
    }

    [Fact]
    public void ForLine_ExemptRate_NetEqualsGross_ZeroVat()
    {
        var split = VatSplit.ForLine(Zl(45.00m), 2m, Vat("zw"));

        Assert.Equal(90.00m, split.Net.Amount);
        Assert.Equal(0m, split.Vat.Amount);
        Assert.Equal(90.00m, split.Gross.Amount);
    }
}
