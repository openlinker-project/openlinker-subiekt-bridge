using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Domain.Customers;

/// <summary>
/// A validated Polish tax identification number (NIP). Construction goes through
/// <see cref="TryCreate"/>, which strips separators, requires exactly 10 digits
/// and verifies the official checksum.
/// </summary>
public readonly struct Nip : IEquatable<Nip>
{
    // Official NIP checksum weights for the first 9 digits.
    private static readonly int[] Weights = { 6, 5, 7, 2, 3, 4, 5, 6, 7 };

    private Nip(string value) => Value = value;

    /// <summary>The canonical 10-digit, separator-free NIP.</summary>
    public string Value { get; }

    public static Result<Nip> TryCreate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Result.Failure<Nip>(new Error("nip.empty", "NIP is required."));

        // Strip common separators (spaces, dashes) and an optional "PL" prefix.
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("PL", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[2..];

        var digits = new string(trimmed.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());

        if (digits.Length != 10 || !digits.All(char.IsDigit))
            return Result.Failure<Nip>(
                new Error("nip.format", "NIP must consist of exactly 10 digits."));

        // Weighted checksum of the first 9 digits, mod 11, must equal the 10th.
        var sum = 0;
        for (var i = 0; i < 9; i++)
            sum += (digits[i] - '0') * Weights[i];

        var control = sum % 11;
        // mod == 10 is invalid (no single check digit can represent it).
        if (control == 10)
            return Result.Failure<Nip>(
                new Error("nip.checksum", "Invalid NIP checksum."));

        var lastDigit = digits[9] - '0';
        if (control != lastDigit)
            return Result.Failure<Nip>(
                new Error("nip.checksum", "Invalid NIP checksum."));

        return Result.Success(new Nip(digits));
    }

    public bool Equals(Nip other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Nip other && Equals(other);

    public override int GetHashCode() => Value?.GetHashCode(StringComparison.Ordinal) ?? 0;

    public static bool operator ==(Nip left, Nip right) => left.Equals(right);

    public static bool operator !=(Nip left, Nip right) => !left.Equals(right);

    public override string ToString() => Value;
}
