using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Infrastructure.Sfera;

/// <summary>
/// Production <see cref="IClock"/> backed by the system wall clock. Registered as a
/// singleton in the composition root; tests substitute a fixed clock.
/// </summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}
