using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Domain.Invoices;

/// <summary>
/// A single document position. Either references a catalogue product by
/// <see cref="ProductSymbol"/>, or carries a one-time <see cref="OneTimeName"/>
/// (PrestaShop product not synced to Subiekt). Quantity and unit gross price are
/// honored from the request; the price may be negative to express a discount line
/// (folded away by <see cref="SalesDocument.FoldDiscounts"/>).
/// </summary>
public sealed class InvoiceLine
{
    public InvoiceLine(
        string productSymbol,
        decimal quantity,
        Money unitGrossPrice,
        VatRate vatRate,
        string? oneTimeName = null)
    {
        if (string.IsNullOrWhiteSpace(productSymbol) && string.IsNullOrWhiteSpace(oneTimeName))
            throw new ArgumentException("A line needs a product symbol or a one-time name.");

        ProductSymbol = productSymbol?.Trim() ?? string.Empty;
        Quantity = quantity;
        UnitGrossPrice = unitGrossPrice;
        VatRate = vatRate;
        OneTimeName = string.IsNullOrWhiteSpace(oneTimeName) ? null : oneTimeName.Trim();
    }

    public string ProductSymbol { get; }

    public decimal Quantity { get; }

    public Money UnitGrossPrice { get; }

    public VatRate VatRate { get; }

    /// <summary>Display name used when the product is not in Subiekt's catalogue.</summary>
    public string? OneTimeName { get; }

    /// <summary>A discount line carries a negative unit gross price.</summary>
    public bool IsDiscount => UnitGrossPrice.Amount < 0m;

    /// <summary>
    /// Effective quantity used for totals. Mirrors the legacy <c>Qty</c> helper:
    /// a non-positive quantity is treated as 1.
    /// </summary>
    public decimal EffectiveQuantity => Quantity <= 0m ? 1m : Quantity;

    /// <summary>Returns a copy of this line with a replaced unit gross price.</summary>
    public InvoiceLine WithUnitGrossPrice(Money newPrice)
        => new(ProductSymbol, Quantity, newPrice, VatRate, OneTimeName);
}
