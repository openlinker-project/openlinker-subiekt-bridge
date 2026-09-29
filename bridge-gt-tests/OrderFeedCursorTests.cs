using Xunit;

/// <summary>
/// The order-feed cursor, pinned.
///
/// The cursor decides whether an order is ever read again. It used to page on
/// the issue date alone with a strict `>`, and because `dok_DataWyst` is a DAY
/// rather than an instant, a shop issuing more than one page of orders in a day
/// had the rest of that day walked past after the first page: not delayed,
/// LOST, with nothing anywhere reporting it. The pair `(date, id)` is what puts
/// the page boundary between two rows instead of inside a group of equals.
/// </summary>
public class OrderFeedCursorTests
{
    [Fact]
    public void A_formatted_cursor_parses_back_to_what_it_was_made_from()
    {
        var when = new DateTime(2026, 9, 29);
        var cursor = BridgeKeys.FormatCursor(when, 4211);
        Assert.True(BridgeKeys.TryParseCursor(cursor, out var watermark, out var lastId));
        Assert.Equal(when.Date, watermark.Date);
        Assert.Equal(4211, lastId);
    }

    [Fact]
    public void The_id_half_survives_the_round_trip_even_at_the_boundaries()
    {
        foreach (var id in new[] { 0, 1, int.MaxValue })
        {
            var cursor = BridgeKeys.FormatCursor(new DateTime(2026, 1, 2), id);
            Assert.True(BridgeKeys.TryParseCursor(cursor, out _, out var parsed));
            Assert.Equal(id, parsed);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("2026-09-29|notanumber")]
    [InlineData("|4211")]
    public void A_cursor_it_cannot_read_is_REFUSED_rather_than_guessed(string cursor)
    {
        // Refusing is what makes the caller start from the beginning, which
        // re-reads and is idempotent. Guessing a watermark would skip orders.
        Assert.False(BridgeKeys.TryParseCursor(cursor, out _, out _));
    }

    [Fact]
    public void A_bare_date_is_ACCEPTED_because_that_is_what_old_cursors_carry()
    {
        // Deliberate, and worth pinning: refusing it would restart every
        // connection's feed from the beginning on the deploy that introduced
        // the pair, re-reading the whole history rather than resuming.
        Assert.True(BridgeKeys.TryParseCursor("2026-09-29", out var watermark, out var lastId));
        Assert.Equal(new DateTime(2026, 9, 29).Date, watermark.Date);
        Assert.Equal(0, lastId);
    }

    [Fact]
    public void Two_rows_on_the_same_DAY_produce_different_cursors()
    {
        // The defect this pair exists to close: on a date-only column these two
        // are indistinguishable, and the second was never read again.
        var day = new DateTime(2026, 9, 29);
        Assert.NotEqual(BridgeKeys.FormatCursor(day, 10), BridgeKeys.FormatCursor(day, 11));
    }
}
