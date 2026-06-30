using Subiekt.Bridge.Domain.Common;
using Xunit;

namespace Subiekt.Bridge.Domain.Tests;

public class VatRateTests
{
    [Theory]
    [InlineData("23", "23", 23)]
    [InlineData("8", "8", 8)]
    [InlineData("5", "5", 5)]
    [InlineData("0", "0", 0)]
    [InlineData(" 23 ", "23", 23)]
    public void TryCreate_NumericRates_ParseAsPercentage(string raw, string symbol, int percent)
    {
        var result = VatRate.TryCreate(raw);

        Assert.True(result.IsSuccess, $"{raw}: {result.Error}");
        Assert.Equal(symbol, result.Value.Symbol);
        Assert.True(result.Value.IsPercentage);
        Assert.Equal(percent, result.Value.Percent);
    }

    [Theory]
    [InlineData("zw", "zw")]
    [InlineData("zw.", "zw")]
    [InlineData("zwolnione", "zw")]
    [InlineData("ZW", "zw")]
    public void TryCreate_ExemptAliases_NormalizeToZw(string raw, string expected)
    {
        var result = VatRate.TryCreate(raw);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value.Symbol);
        Assert.False(result.Value.IsPercentage);
        Assert.Null(result.Value.Percent);
    }

    [Theory]
    [InlineData("np", "nieop.")]
    [InlineData("np.", "nieop.")]
    [InlineData("nieopodatkowane", "nieop.")]
    [InlineData("nieop.", "nieop.")]
    [InlineData("NP", "nieop.")]
    public void TryCreate_NotTaxedAliases_NormalizeToNieop(string raw, string expected)
    {
        var result = VatRate.TryCreate(raw);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value.Symbol);
        Assert.False(result.Value.IsPercentage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryCreate_Empty_Fails(string? raw)
    {
        var result = VatRate.TryCreate(raw);

        Assert.True(result.IsFailure);
        Assert.Equal("vat.empty", result.Error.Code);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("twenty-three")]
    public void TryCreate_Garbage_Fails(string raw)
    {
        var result = VatRate.TryCreate(raw);

        Assert.True(result.IsFailure);
        Assert.Equal("vat.unrecognized", result.Error.Code);
    }
}
