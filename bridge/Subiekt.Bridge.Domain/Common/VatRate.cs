namespace Subiekt.Bridge.Domain.Common;

/// <summary>
/// A normalized Polish VAT rate. Parses the OL-contract <c>taxRate</c> strings
/// ("23", "8", "5", "0", "zw", "np" and a few aliases) into a canonical symbol
/// that matches Subiekt's <c>StawkiVat.Symbol</c> column.
/// <para>
/// Ported from the legacy <c>DokumentySprzedazyService.LookupStawkaVatId</c>
/// alias switch: "np"/"np."/"nieopodatkowane" -&gt; "nieop."; "zwolnione"/"zw."
/// -&gt; "zw"; numeric percentages pass through unchanged.
/// </para>
/// </summary>
public readonly struct VatRate : IEquatable<VatRate>
{
    private VatRate(string symbol, bool isPercentage, decimal? percent)
    {
        Symbol = symbol;
        IsPercentage = isPercentage;
        Percent = percent;
    }

    /// <summary>Canonical symbol matching Subiekt's StawkiVat.Symbol ("23","8","5","0","zw","nieop.").</summary>
    public string Symbol { get; }

    /// <summary>True for numeric percentage rates; false for special symbols ("zw", "nieop.").</summary>
    public bool IsPercentage { get; }

    /// <summary>The numeric percent (e.g. 23) when <see cref="IsPercentage"/> is true; otherwise null.</summary>
    public decimal? Percent { get; }

    public static Result<VatRate> TryCreate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Result.Failure<VatRate>(new Error("vat.empty", "VAT rate is required."));

        // Mirror the legacy normalization: trim + lower-invariant, then map aliases.
        var normalized = raw.Trim().ToLowerInvariant() switch
        {
            "np" or "np." or "nieopodatkowane" or "nieop." => "nieop.",
            "zwolnione" or "zw." or "zw" => "zw",
            var s => s
        };

        switch (normalized)
        {
            case "zw":
                return Result.Success(new VatRate("zw", isPercentage: false, percent: null));
            case "nieop.":
                return Result.Success(new VatRate("nieop.", isPercentage: false, percent: null));
        }

        // Numeric percentage. Accept only simple rate strings ("23","8","5","0",
        // optionally "23.0"). AllowDecimalPoint (no thousands separators, no stray
        // whitespace) prevents "1,000" parsing as 1000 or " 23 " sneaking through.
        if (decimal.TryParse(normalized, System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var percent)
            && percent >= 0m)
        {
            // Canonical symbol drops a trailing ".0" so "23" stays "23".
            var symbol = percent == Math.Truncate(percent)
                ? ((long)percent).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : percent.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Result.Success(new VatRate(symbol, isPercentage: true, percent: percent));
        }

        return Result.Failure<VatRate>(
            new Error("vat.unrecognized", $"Unrecognized VAT rate '{raw}'."));
    }

    public bool Equals(VatRate other) => string.Equals(Symbol, other.Symbol, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is VatRate other && Equals(other);

    public override int GetHashCode() => Symbol?.GetHashCode(StringComparison.Ordinal) ?? 0;

    public static bool operator ==(VatRate left, VatRate right) => left.Equals(right);

    public static bool operator !=(VatRate left, VatRate right) => !left.Equals(right);

    public override string ToString() => Symbol;
}
