using Xunit;

/// <summary>
/// Placeholder credentials count as UNSET.
///
/// The bridge's security rule is "unconfigured means CLOSED", and the docs tell
/// an operator to copy `appsettings.example.json`. That file ships visible
/// stand-ins for the three credentials, so without this an operator who copied
/// it and forgot to edit got a bridge guarded by a secret printed in a public
/// repository - on routes that issue FS/PA documents, create ZK orders and move
/// stock.
///
/// The certificate path is here for a different failure: left at its stand-in
/// it made `HttpsConfigured` true and the process died binding a file that does
/// not exist, which is neither of the two documented behaviours.
/// </summary>
public class BridgeConfigPlaceholderTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CHANGE-ME")]
    [InlineData("change-me")]          // an operator editing by hand
    [InlineData("  CHANGE-ME  ")]
    [InlineData("C:\\path\\to\\bridge.pfx")]
    public void Blank_and_stand_in_values_read_as_unset(string value)
    {
        Assert.True(BridgeConfig.IsUnsetOrPlaceholder(value));
    }

    [Theory]
    [InlineData("a-real-token")]
    [InlineData("CHANGE-ME-BUT-NOT-QUITE")]   // contains it, is not it
    [InlineData("C:\\certs\\bridge.pfx")]
    public void A_value_an_operator_chose_reads_as_set(string value)
    {
        Assert.False(BridgeConfig.IsUnsetOrPlaceholder(value));
    }
}
