using Subiekt.Bridge.Domain.Invoices;
using Xunit;

namespace Subiekt.Bridge.Domain.Tests;

public class BranchSelectionTests
{
    // --- both fields absent -> success(null) = "no selection", implicit defaults apply ---

    [Fact]
    public void TryCreate_BothAbsent_SucceedsWithNull()
    {
        var result = BranchSelection.TryCreate(null, null);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    // --- oddzialId alone -> rejected (Sfera's implicit default cash-register does not
    //     scope to a non-default branch — live-verified, see docs/spikes) ---

    [Fact]
    public void TryCreate_OddzialWithoutStanowisko_Fails()
    {
        var result = BranchSelection.TryCreate(100001, null);

        Assert.True(result.IsFailure);
        Assert.Equal("branch.stanowisko", result.Error.Code);
    }

    // --- stanowiskoKasoweId alone -> allowed (keeps the document's default branch) ---

    [Fact]
    public void TryCreate_StanowiskoWithoutOddzial_Succeeds()
    {
        var result = BranchSelection.TryCreate(null, 100065);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Null(result.Value!.OddzialId);
        Assert.Equal(100065, result.Value.StanowiskoKasoweId);
    }

    // --- both present -> allowed (cross-consistency deferred to issuance time) ---

    [Fact]
    public void TryCreate_BothPresent_Succeeds()
    {
        var result = BranchSelection.TryCreate(100001, 100066);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(100001, result.Value!.OddzialId);
        Assert.Equal(100066, result.Value.StanowiskoKasoweId);
    }

    // --- non-positive ids ---

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void TryCreate_NonPositiveStanowisko_Fails(int stanowiskoKasoweId)
    {
        var result = BranchSelection.TryCreate(100001, stanowiskoKasoweId);

        Assert.True(result.IsFailure);
        Assert.Equal("branch.stanowisko", result.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void TryCreate_NonPositiveOddzial_Fails(int oddzialId)
    {
        var result = BranchSelection.TryCreate(oddzialId, 100065);

        Assert.True(result.IsFailure);
        Assert.Equal("branch.oddzial", result.Error.Code);
    }
}
