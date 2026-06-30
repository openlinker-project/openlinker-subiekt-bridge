using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Domain.Invoices;

/// <summary>
/// A gross amount decomposed into net + VAT for a single VAT rate.
/// Mirrors the intent of the legacy <c>Przelicz()</c> recompute, which derives
/// netto/VAT from manual gross prices, expressed here as a pure function.
/// </summary>
public readonly record struct VatSplit(VatRate Rate, Money Gross, Money Net, Money Vat)
{
    /// <summary>
    /// Split a gross amount for a VAT rate into net and VAT components by deriving
    /// the net from the aggregate gross (net = round(gross / (1 + p/100))).
    /// Special (non-percentage) rates ("zw", "nieop.") carry zero VAT: gross == net.
    /// <para>
    /// This is the aggregate-gross variant. It is NOT bit-for-bit faithful to Subiekt's
    /// <c>Przelicz()</c>, which rounds the net per unit first; use <see cref="ForLine"/>
    /// for the Przelicz-faithful per-line computation.
    /// </para>
    /// </summary>
    public static VatSplit FromGross(Money gross, VatRate rate)
    {
        if (!rate.IsPercentage || rate.Percent is not { } percent || percent == 0m)
        {
            var zeroVat = Money.Zero(gross.Currency);
            return new VatSplit(rate, gross, gross, zeroVat);
        }

        // net = gross / (1 + p/100); vat = gross - net. Round both to 2 places.
        var divisor = 1m + (percent / 100m);
        var net = gross.Multiply(1m / divisor).Round();
        var vat = gross.Subtract(net).Round();
        return new VatSplit(rate, gross, net, vat);
    }

    /// <summary>
    /// Compute the net/VAT split for a single document line the way Subiekt/Sfera's
    /// <c>Przelicz()</c> does: round the net per UNIT, multiply by quantity, then derive
    /// VAT from the rounded net. The resulting gross (net + vat) may differ from
    /// <paramref name="unitGross"/> * <paramref name="quantity"/> by a grosz.
    /// <code>
    /// netUnit   = round(unitGross / (1 + p/100), 2, AwayFromZero)
    /// lineNet   = netUnit * quantity
    /// lineVat   = round(lineNet * p/100, 2, AwayFromZero)
    /// lineGross = lineNet + lineVat
    /// </code>
    /// For non-percentage rates ("zw", "nieop.") or 0%: net = unitGross * quantity,
    /// vat = 0, gross = net.
    /// </summary>
    public static VatSplit ForLine(Money unitGross, decimal quantity, VatRate rate)
    {
        if (!rate.IsPercentage || rate.Percent is not { } percent || percent == 0m)
        {
            // Round the line net to 2 places too (fractional quantity / >2dp products),
            // so an exempt line never carries sub-grosz precision into the totals.
            var net = unitGross.Multiply(quantity).Round();
            var zeroVat = Money.Zero(unitGross.Currency);
            return new VatSplit(rate, net, net, zeroVat);
        }

        // Per-unit net rounded away-from-zero, then VAT from the rounded line net.
        var divisor = 1m + (percent / 100m);
        var netUnit = unitGross.Multiply(1m / divisor).Round();
        var lineNet = netUnit.Multiply(quantity);
        var lineVat = lineNet.Multiply(percent / 100m).Round();
        var lineGross = lineNet.Add(lineVat);
        return new VatSplit(rate, lineGross, lineNet, lineVat);
    }
}
