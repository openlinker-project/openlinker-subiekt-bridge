namespace Subiekt.Bridge.Infrastructure.Persistence;

/// <summary>
/// Configuration for <see cref="AtomicIdempotencyStore"/>. Bound from config in
/// the composition root (Faza 3B). The default file name matches the legacy
/// <c>idempotency-store.json</c>; <see cref="Directory"/> defaults to the
/// current directory but is meant to be pointed at a writable location outside
/// the source tree.
/// </summary>
public sealed class IdempotencyStoreOptions
{
    /// <summary>Directory that holds the store file. Created if missing.</summary>
    public string Directory { get; set; } = ".";

    /// <summary>Store file name within <see cref="Directory"/>.</summary>
    public string FileName { get; set; } = "idempotency-store.json";
}
