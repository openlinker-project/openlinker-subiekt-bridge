namespace Subiekt.Bridge.Domain.Common;

/// <summary>
/// A monetary amount in a given currency. Value object: two instances are equal
/// when both amount and (case-insensitive) currency match. Arithmetic is only
/// allowed within the same currency — mixing currencies throws.
/// </summary>
public readonly struct Money : IEquatable<Money>
{
    public Money(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency is required.", nameof(currency));

        Amount = amount;
        Currency = currency.Trim().ToUpperInvariant();
    }

    public decimal Amount { get; }

    public string Currency { get; }

    public static Money Zero(string currency) => new(0m, currency);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    /// <summary>Scale the amount by a unitless factor (e.g. a discount fraction).</summary>
    public Money Multiply(decimal factor) => new(Amount * factor, Currency);

    /// <summary>
    /// Round to <paramref name="decimals"/> places. Defaults to away-from-zero
    /// rounding to match the legacy line-price rounding (MidpointRounding.AwayFromZero).
    /// </summary>
    public Money Round(int decimals = 2, MidpointRounding mode = MidpointRounding.AwayFromZero)
        => new(Math.Round(Amount, decimals, mode), Currency);

    private void EnsureSameCurrency(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Cannot operate on amounts in different currencies: '{Currency}' vs '{other.Currency}'.");
    }

    public bool Equals(Money other)
        => Amount == other.Amount && string.Equals(Currency, other.Currency, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Money other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Amount, Currency);

    public static bool operator ==(Money left, Money right) => left.Equals(right);

    public static bool operator !=(Money left, Money right) => !left.Equals(right);

    public static Money operator +(Money left, Money right) => left.Add(right);

    public static Money operator -(Money left, Money right) => left.Subtract(right);

    public override string ToString() => $"{Amount:0.##} {Currency}";
}
