namespace Subiekt.Bridge.Domain.Common;

/// <summary>
/// A domain error: a machine-readable <see cref="Code"/> plus a human-readable
/// <see cref="Message"/>. Used to model expected validation failures as values
/// (via <see cref="Result"/>) instead of throwing exceptions.
/// </summary>
public readonly record struct Error(string Code, string Message)
{
    /// <summary>The canonical "no error" value carried by a successful result.</summary>
    public static readonly Error None = new(string.Empty, string.Empty);

    public override string ToString() => string.IsNullOrEmpty(Code) ? Message : $"{Code}: {Message}";
}
