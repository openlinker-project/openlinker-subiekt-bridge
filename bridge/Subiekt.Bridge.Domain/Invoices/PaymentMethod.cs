namespace Subiekt.Bridge.Domain.Invoices;

/// <summary>
/// The payment method a caller can explicitly select for a sales document.
/// Mirrors the OpenLinker-neutral vocabulary (<c>cash</c> / <c>transfer</c>);
/// the Sfera adapter maps each value to a configured <c>FormaPlatnosci</c> row.
/// </summary>
public enum PaymentMethod
{
    /// <summary>Gotówka — immediate payment for the full document amount.</summary>
    Cash,

    /// <summary>Przelew — deferred payment to a selected seller bank account.</summary>
    Transfer
}
