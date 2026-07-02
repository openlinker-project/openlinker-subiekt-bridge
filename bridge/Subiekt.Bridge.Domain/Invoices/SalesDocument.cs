using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Domain.Invoices;

/// <summary>
/// Computed fiscal dates for a sales document.
/// <list type="bullet">
/// <item><see cref="DataSprzedazy"/> — sale/VAT date = the issue date (may be in the past).</item>
/// <item><see cref="DataWydania"/> — issue/dispatch date = "now" (stock moves today).</item>
/// </list>
/// </summary>
public readonly record struct FiscalDates(DateTimeOffset DataSprzedazy, DateTimeOffset DataWydania);

/// <summary>
/// A sales document aggregate (faktura / paragon). Holds the lines, the buyer
/// reference, currency and issue date, and exposes the business rules extracted
/// from the legacy <c>DokumentySprzedazyService</c> as pure, deterministic methods.
/// </summary>
public sealed class SalesDocument
{
    private readonly List<InvoiceLine> _lines;

    private SalesDocument(
        DocumentType documentType,
        int buyerId,
        string currency,
        DateTimeOffset issueDate,
        IReadOnlyList<InvoiceLine> lines,
        PaymentSelection? payment)
    {
        DocumentType = documentType;
        BuyerId = buyerId;
        Currency = currency;
        IssueDate = issueDate;
        _lines = lines.ToList();
        Payment = payment;
    }

    public DocumentType DocumentType { get; }

    /// <summary>Provider-side buyer (kontrahent) id.</summary>
    public int BuyerId { get; }

    public string Currency { get; }

    public DateTimeOffset IssueDate { get; }

    public IReadOnlyList<InvoiceLine> Lines => _lines;

    /// <summary>
    /// Explicit payment selection (issue #1). Null means "no selection" — the
    /// provider's default payment behavior applies unchanged.
    /// </summary>
    public PaymentSelection? Payment { get; }

    public static Result<SalesDocument> Create(
        DocumentType documentType,
        int buyerId,
        string currency,
        DateTimeOffset issueDate,
        IReadOnlyList<InvoiceLine> lines,
        PaymentSelection? payment = null)
    {
        if (buyerId <= 0)
            return Result.Failure<SalesDocument>(new Error("doc.buyer", "A valid buyer id is required."));
        if (string.IsNullOrWhiteSpace(currency))
            return Result.Failure<SalesDocument>(new Error("doc.currency", "Currency is required."));
        if (lines is null || lines.Count == 0)
            return Result.Failure<SalesDocument>(new Error("doc.lines", "At least one line is required."));

        // A paragon keeps the provider's immediate-payment default path; an explicit
        // selection on PA would be silently ignored downstream, so reject it loudly.
        if (payment is not null && documentType == DocumentType.PA)
            return Result.Failure<SalesDocument>(
                new Error("doc.payment.pa", "An explicit payment selection is not supported for a paragon (PA)."));

        var normalizedCurrency = currency.Trim().ToUpperInvariant();
        if (lines.Any(l => !string.Equals(l.UnitGrossPrice.Currency, normalizedCurrency, StringComparison.Ordinal)))
            return Result.Failure<SalesDocument>(
                new Error("doc.currency.mismatch", "All line prices must use the document currency."));

        return Result.Success(new SalesDocument(documentType, buyerId, normalizedCurrency, issueDate, lines, payment));
    }

    /// <summary>
    /// Compute the fiscal dates from the issue date and the clock.
    /// <para>
    /// Ported faithfully from the legacy service (steps around lines 79-86):
    /// the VAT/sale month is driven by the request's issue date
    /// (<c>DataSprzedazy = dto.IssueDate ?? now</c>), while the dispatch/entry date
    /// (<c>DataWydaniaWystawienia</c>) stays "now" so Subiekt still moves stock today
    /// rather than retroactively. Here the issue date is already resolved on the
    /// aggregate, so <c>DataSprzedazy = IssueDate</c> and <c>DataWydania = clock.Now</c>.
    /// </para>
    /// </summary>
    public FiscalDates ComputeFiscalDates(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new FiscalDates(DataSprzedazy: IssueDate, DataWydania: clock.Now);
    }

    /// <summary>
    /// Fold discount lines (negative unit gross price) proportionally into the
    /// positive lines' gross prices, returning a document with no negative positions.
    /// <para>
    /// Ported faithfully from <c>DokumentySprzedazyService.Create</c> (lines ~98-167).
    /// Subiekt rejects a negative position and a document-level rabat cannot override
    /// our manual per-line prices, so instead of adding the discount as its own line
    /// we scale every positive line price by a single proportional factor:
    /// </para>
    /// <code>
    /// Qty(l)      = l.Quantity &lt;= 0 ? 1 : l.Quantity            // EffectiveQuantity
    /// posTotal    = Σ (price * Qty)   over lines with price &gt; 0
    /// discTotal   = Σ (|price| * Qty) over lines with price &lt; 0
    /// factor      = (posTotal &gt; 0 &amp;&amp; discTotal &gt; 0 &amp;&amp; discTotal &lt; posTotal)
    ///                 ? (posTotal - discTotal) / posTotal
    ///                 : 1
    /// effPrice(l) = round(price * factor, 2, AwayFromZero)        // per positive line
    /// </code>
    /// <para>
    /// Discount lines are dropped; positive lines keep their VAT rate and quantity so
    /// totals and per-rate VAT stay exact without any negative position or rabat/manual
    /// price conflict. When the guard fails (no discount, discount &gt;= positive total,
    /// or non-positive positive total) the factor is 1 and positive lines pass through
    /// unchanged (discount lines still dropped).
    /// </para>
    /// </summary>
    public SalesDocument FoldDiscounts()
    {
        var posTotal = 0m;
        var discTotal = 0m;
        foreach (var line in _lines)
        {
            var lineTotal = line.UnitGrossPrice.Amount * line.EffectiveQuantity;
            if (line.UnitGrossPrice.Amount > 0m)
                posTotal += lineTotal;
            else if (line.UnitGrossPrice.Amount < 0m)
                discTotal += Math.Abs(lineTotal);
        }

        var factor = (posTotal > 0m && discTotal > 0m && discTotal < posTotal)
            ? (posTotal - discTotal) / posTotal
            : 1m;

        var folded = new List<InvoiceLine>(_lines.Count);
        foreach (var line in _lines)
        {
            if (line.UnitGrossPrice.Amount < 0m)
                continue; // discount line is folded into the factor, not emitted

            if (factor == 1m)
            {
                folded.Add(line);
                continue;
            }

            var effPrice = line.UnitGrossPrice.Multiply(factor).Round();
            folded.Add(line.WithUnitGrossPrice(effPrice));
        }

        return new SalesDocument(DocumentType, BuyerId, Currency, IssueDate, folded, Payment);
    }

    /// <summary>
    /// Net/VAT split per VAT rate over the (folded) lines. Mirrors Subiekt/Sfera's
    /// <c>Przelicz()</c> bit-for-bit: each positive line is split with
    /// <see cref="VatSplit.ForLine"/> (per-unit net rounded away-from-zero, VAT derived
    /// from the rounded line net), and the per-line splits are then aggregated per
    /// VAT-rate symbol by summing Net, Vat and Gross. Discount (negative) lines are
    /// skipped — they are folded into the positive lines by <see cref="FoldDiscounts"/>.
    /// </summary>
    public IReadOnlyList<VatSplit> ComputeVatSplits()
    {
        var byRate = new Dictionary<string, VatSplit>();
        foreach (var line in _lines)
        {
            if (line.UnitGrossPrice.Amount < 0m)
                continue;

            var lineSplit = VatSplit.ForLine(line.UnitGrossPrice, line.EffectiveQuantity, line.VatRate);
            if (byRate.TryGetValue(line.VatRate.Symbol, out var acc))
            {
                byRate[line.VatRate.Symbol] = acc with
                {
                    Net = acc.Net.Add(lineSplit.Net),
                    Vat = acc.Vat.Add(lineSplit.Vat),
                    Gross = acc.Gross.Add(lineSplit.Gross),
                };
            }
            else
            {
                byRate[line.VatRate.Symbol] = lineSplit;
            }
        }

        return byRate.Values.ToList();
    }
}
