namespace Subiekt.Bridge.Domain.Common;

/// <summary>
/// Abstraction over "now" so domain rules that depend on the current time
/// (e.g. fiscal date computation) stay deterministic and unit-testable.
/// </summary>
public interface IClock
{
    DateTimeOffset Now { get; }
}
