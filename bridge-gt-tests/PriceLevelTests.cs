using Xunit;

/// <summary>
/// The price level, and the two numbering systems that meet in it.
///
/// Subiekt GT carries up to ten operator-named sale price levels. Everything
/// here was hardcoded to the first one, so a shop keeping its e-commerce price
/// on a dedicated level published the WRONG price to the marketplace and said
/// nothing about it.
///
/// The hazard the tests below exist for is the off-by-one: the operator and
/// SQL count from 1 while the Sfera object model counts from 0, and reading
/// one level while writing another is a price that means a different thing
/// depending on which column you look at.
/// </summary>
public class PriceLevelTests
{
    [Fact]
    public void Default_is_the_first_level_so_an_existing_install_is_unchanged()
    {
        Assert.Equal(1, PriceLevel.Configured);
        Assert.Equal("tc_CenaNetto1", PriceLevel.NettoColumn);
        Assert.Equal("tc_CenaBrutto1", PriceLevel.BruttoColumn);
        Assert.Equal("tc_IdWaluta1", PriceLevel.CurrencyColumn);
    }

    /// <summary>
    /// The correspondence stated once in PriceLevel.cs, asserted: the Sfera id
    /// is the operator's number minus one.
    /// </summary>
    [Fact]
    public void Sfera_id_is_one_below_the_operator_facing_number()
    {
        Assert.Equal(PriceLevel.Configured - 1, PriceLevel.SferaLevelId);
    }

    /// <summary>
    /// The read columns and the write target must name the SAME level. This is
    /// the property that cannot be recovered once broken: nothing downstream
    /// can tell a price read from level 1 and written to level 2 apart from a
    /// price that simply changed.
    /// </summary>
    [Fact]
    public void Read_columns_and_write_target_name_one_level()
    {
        var n = PriceLevel.SferaLevelId + 1;
        Assert.Equal($"tc_CenaNetto{n}", PriceLevel.NettoColumn);
        Assert.Equal($"tc_CenaBrutto{n}", PriceLevel.BruttoColumn);
        Assert.Contains(PriceLevel.NettoColumn, PriceLevel.SelectColumns);
        Assert.Contains(PriceLevel.BruttoColumn, PriceLevel.SelectColumns);
    }

    /// <summary>
    /// SelectColumns is INTERPOLATED into SQL, because a column name cannot be
    /// a parameter. That is only safe while the value can render nothing but a
    /// fixed column name - so this asserts the shape rather than trusting the
    /// clamp elsewhere to stay in place.
    /// </summary>
    [Fact]
    public void Select_fragment_can_contain_nothing_but_column_names()
    {
        Assert.Matches(@"^c\.tc_CenaNetto(10|[1-9]), c\.tc_CenaBrutto(10|[1-9])$", PriceLevel.SelectColumns);
    }
}
