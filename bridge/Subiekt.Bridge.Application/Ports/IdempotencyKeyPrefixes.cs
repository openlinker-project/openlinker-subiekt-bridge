namespace Subiekt.Bridge.Application.Ports;

/// <summary>
/// Document-type namespaces for the SHARED <see cref="IIdempotencyStore"/> keyspace.
/// The store has one flat keyspace with no document-type namespacing of its own, so
/// callers prefix the raw OL idempotency key by document type to guarantee two domains
/// can never collide on the same raw key:
/// <list type="bullet">
/// <item><see cref="Fv"/> — sales documents (faktura / paragon).</item>
/// <item><see cref="Pw"/> — warehouse receipts (przychód wewnętrzny / PW).</item>
/// </list>
/// <para>
/// MIGRATION: pre-existing bare (un-prefixed) keys are invoice keys; they are rewritten
/// to <see cref="Fv"/>-prefixed keys once at store <c>Load()</c> (AC-I4). The
/// namespacing/migration exactly-once guarantee assumes ONE bridge process per
/// idempotency-store file (AC-I6).
/// </para>
/// </summary>
public static class IdempotencyKeyPrefixes
{
    public const string Fv = "fv:";
    public const string Pw = "pw:";
}
