using Subiekt.Bridge.Domain.Customers;
using Xunit;

namespace Subiekt.Bridge.Domain.Tests;

public class NipTests
{
    [Theory]
    [InlineData("5260001246")]
    [InlineData("1234563218")]
    [InlineData("5260001223")]
    [InlineData("6790001237")]
    public void TryCreate_ValidNip_Succeeds(string raw)
    {
        var result = Nip.TryCreate(raw);

        Assert.True(result.IsSuccess, $"expected '{raw}' to be valid: {result.Error}");
        Assert.Equal(raw, result.Value.Value);
    }

    [Theory]
    [InlineData("526-000-12-46")]
    [InlineData("526 000 12 46")]
    [InlineData("PL5260001246")]
    [InlineData("  5260001246  ")]
    public void TryCreate_StripsSeparatorsAndPrefix(string raw)
    {
        var result = Nip.TryCreate(raw);

        Assert.True(result.IsSuccess, $"expected '{raw}' to normalize to a valid NIP: {result.Error}");
        Assert.Equal("5260001246", result.Value.Value);
    }

    [Theory]
    [InlineData("5260001245")] // wrong check digit
    [InlineData("1234563210")] // wrong check digit
    public void TryCreate_WrongChecksum_Fails(string raw)
    {
        var result = Nip.TryCreate(raw);

        Assert.True(result.IsFailure);
        Assert.Equal("nip.checksum", result.Error.Code);
    }

    [Fact]
    public void TryCreate_ChecksumModuloTen_Fails()
    {
        // Prefix 000000003 has weighted-sum % 11 == 10, which is not a valid check digit.
        var result = Nip.TryCreate("0000000030");

        Assert.True(result.IsFailure);
        Assert.Equal("nip.checksum", result.Error.Code);
    }

    [Theory]
    [InlineData("123")]          // too short
    [InlineData("12345678901")]  // too long
    [InlineData("526000124")]    // 9 digits
    public void TryCreate_WrongLength_Fails(string raw)
    {
        var result = Nip.TryCreate(raw);

        Assert.True(result.IsFailure);
        Assert.Equal("nip.format", result.Error.Code);
    }

    [Theory]
    [InlineData("52600012AB")]
    [InlineData("ABCDEFGHIJ")]
    public void TryCreate_NonDigits_Fails(string raw)
    {
        var result = Nip.TryCreate(raw);

        Assert.True(result.IsFailure);
        Assert.Equal("nip.format", result.Error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryCreate_Empty_Fails(string? raw)
    {
        var result = Nip.TryCreate(raw);

        Assert.True(result.IsFailure);
        Assert.Equal("nip.empty", result.Error.Code);
    }
}
