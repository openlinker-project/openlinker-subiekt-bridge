using System.Collections.Concurrent;
using System.Text.Json;
using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Infrastructure.Persistence;

/// <summary>
/// File-backed <see cref="IIdempotencyStore"/> that keeps an in-memory
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> cache and persists the whole
/// map as JSON on disk. Writes are ATOMIC: the JSON is written to a temp file in
/// the same directory and then renamed over the target with
/// <see cref="File.Move(string,string,bool)"/>, so a crash mid-write cannot
/// corrupt or truncate the existing map.
/// </summary>
public sealed class AtomicIdempotencyStore : IIdempotencyStore
{
    // Persisted shape — mirrors the legacy { Id, Numer } entry so an existing
    // idempotency-store.json keeps loading after the refactor.
    private sealed class Entry
    {
        public int Id { get; set; }
        public string? Numer { get; set; }
    }

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly ConcurrentDictionary<string, Entry> _map = new(StringComparer.Ordinal);
    private readonly string _path;
    private readonly object _ioLock = new();

    public AtomicIdempotencyStore(IdempotencyStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var dir = string.IsNullOrWhiteSpace(options.Directory) ? "." : options.Directory;
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, options.FileName);

        Load();
    }

    public Task<Result<IdempotentInvoice?>> TryGetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return Task.FromResult(Result.Success<IdempotentInvoice?>(null));

        if (_map.TryGetValue(key, out var e))
            return Task.FromResult(Result.Success<IdempotentInvoice?>(new IdempotentInvoice(e.Id, e.Numer)));

        return Task.FromResult(Result.Success<IdempotentInvoice?>(null));
    }

    public Task<Result> StoreAsync(string key, IdempotentInvoice invoice, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key) || invoice.ProviderInvoiceId <= 0)
            return Task.FromResult(Result.Success());

        try
        {
            // Mutate the cache and persist as ONE critical section so the in-memory
            // map and the on-disk snapshot stay coherent under concurrent stores.
            // (Persist() re-enters _ioLock, which is fine — C# locks are reentrant.)
            lock (_ioLock)
            {
                _map[key] = new Entry { Id = invoice.ProviderInvoiceId, Numer = invoice.ProviderInvoiceNumber };
                Persist();
            }

            return Task.FromResult(Result.Success());
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure(
                new Error("idempotency.persist_failed", $"Failed to persist idempotency store: {ex.Message}")));
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;

            var json = File.ReadAllText(_path);
            var data = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json);
            if (data is null)
                return;

            foreach (var kv in data)
                _map[kv.Key] = kv.Value;
        }
        catch
        {
            // A corrupt or unreadable store must not prevent startup; start empty.
            // (The atomic-rename write below guarantees we never produce a
            // half-written file, so this is a last-resort safety net.)
        }
    }

    private void Persist()
    {
        lock (_ioLock)
        {
            var json = JsonSerializer.Serialize(_map, WriteOptions);

            // Temp file MUST live in the same directory as the target so the
            // rename is a same-volume atomic operation.
            var dir = Path.GetDirectoryName(_path);
            var tempPath = Path.Combine(
                string.IsNullOrEmpty(dir) ? "." : dir,
                $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");

            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _path, overwrite: true);
            }
            catch
            {
                // Best-effort cleanup of the temp file on failure; rethrow so the
                // caller surfaces a failed Result.
                TryDelete(tempPath);
                throw;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}
