namespace Subiekt.Bridge.Domain.Invoices;

/// <summary>
/// The kind of sales document. FV = faktura sprzedaży (VAT invoice),
/// PA = paragon (receipt). Maps to the legacy factory methods
/// <c>UtworzFaktureSprzedazy</c> / <c>UtworzParagon</c>.
/// </summary>
public enum DocumentType
{
    /// <summary>Faktura sprzedaży (VAT invoice).</summary>
    FV,

    /// <summary>Paragon (receipt).</summary>
    PA
}
