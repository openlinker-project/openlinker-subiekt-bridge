using Subiekt.Bridge.Domain.Invoices;
using Xunit;

namespace Subiekt.Bridge.Domain.Tests;

public class PaymentSelectionTests
{
    // --- both fields absent -> success(null) = "no selection", defaults apply ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryCreate_BothAbsent_SucceedsWithNull(string? method)
    {
        var result = PaymentSelection.TryCreate(method, null);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    // --- bankAccountId without an explicit method -> rejected ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryCreate_BankAccountWithoutMethod_Fails(string? method)
    {
        var result = PaymentSelection.TryCreate(method, 100007);

        Assert.True(result.IsFailure);
        Assert.Equal("payment.bankAccount", result.Error.Code);
    }

    // --- cash ---

    [Theory]
    [InlineData("cash")]
    [InlineData("CASH")]
    [InlineData("  Cash  ")]
    public void TryCreate_CashWithoutAccount_Succeeds(string method)
    {
        var result = PaymentSelection.TryCreate(method, null);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(PaymentMethod.Cash, result.Value!.Method);
        Assert.Null(result.Value.BankAccountId);
    }

    [Fact]
    public void TryCreate_CashWithAccount_Fails()
    {
        var result = PaymentSelection.TryCreate("cash", 100007);

        Assert.True(result.IsFailure);
        Assert.Equal("payment.bankAccount", result.Error.Code);
    }

    // --- transfer ---

    [Theory]
    [InlineData("transfer")]
    [InlineData("TRANSFER")]
    [InlineData("  Transfer ")]
    public void TryCreate_TransferWithAccount_Succeeds(string method)
    {
        var result = PaymentSelection.TryCreate(method, 100007);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(PaymentMethod.Transfer, result.Value!.Method);
        Assert.Equal(100007, result.Value.BankAccountId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void TryCreate_TransferWithoutPositiveAccount_Fails(int? bankAccountId)
    {
        var result = PaymentSelection.TryCreate("transfer", bankAccountId);

        Assert.True(result.IsFailure);
        Assert.Equal("payment.bankAccount", result.Error.Code);
    }

    // --- unknown method ---

    [Theory]
    [InlineData("card")]
    [InlineData("przelew")]
    [InlineData("gotówka")]
    public void TryCreate_UnknownMethod_Fails(string method)
    {
        var result = PaymentSelection.TryCreate(method, null);

        Assert.True(result.IsFailure);
        Assert.Equal("payment.method", result.Error.Code);
    }
}
