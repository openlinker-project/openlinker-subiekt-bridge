using Subiekt.Bridge.Domain.Invoices;
using Xunit;

namespace Subiekt.Bridge.Domain.Tests;

public class CashRegisterSelectionTests
{
    // --- absent -> success(null) = "no selection", implicit default applies ---

    [Fact]
    public void TryCreate_Absent_SucceedsWithNull()
    {
        var result = CashRegisterSelection.TryCreate(null);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    // --- positive id -> allowed ---

    [Fact]
    public void TryCreate_PositiveId_Succeeds()
    {
        var result = CashRegisterSelection.TryCreate(100065);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(100065, result.Value!.StanowiskoKasoweId);
    }

    // --- non-positive ids -> rejected ---

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void TryCreate_NonPositiveId_Fails(int stanowiskoKasoweId)
    {
        var result = CashRegisterSelection.TryCreate(stanowiskoKasoweId);

        Assert.True(result.IsFailure);
        Assert.Equal("cashRegister.stanowisko", result.Error.Code);
    }
}
