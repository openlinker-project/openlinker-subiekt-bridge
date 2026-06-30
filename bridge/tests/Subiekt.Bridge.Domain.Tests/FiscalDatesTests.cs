using Subiekt.Bridge.Domain.Common;
using Subiekt.Bridge.Domain.Invoices;
using Xunit;

namespace Subiekt.Bridge.Domain.Tests;

public class FiscalDatesTests
{
    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset now) => Now = now;
        public DateTimeOffset Now { get; }
    }

    private static InvoiceLine SampleLine()
        => new("A", 1, new Money(100m, "PLN"), VatRate.TryCreate("23").Value);

    [Fact]
    public void ComputeFiscalDates_SaleDateIsIssueDate_DispatchDateIsNow()
    {
        // Past-dated issue date (e.g. order paid earlier) but stock moves "today".
        var issue = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 6, 20, 12, 30, 0, TimeSpan.Zero);
        var doc = SalesDocument.Create(DocumentType.FV, 1, "PLN", issue, new[] { SampleLine() }).Value;

        var dates = doc.ComputeFiscalDates(new FixedClock(now));

        Assert.Equal(issue, dates.DataSprzedazy); // VAT/sale month = issue date
        Assert.Equal(now, dates.DataWydania);     // dispatch/entry = now
    }
}
