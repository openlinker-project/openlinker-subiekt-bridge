using Microsoft.Extensions.Logging;

namespace Subiekt.Bridge.Infrastructure.Sfera;

/// <summary>
/// Reflective Sfera service that creates a REAL warehouse-receipt document
/// (PW — <c>IPrzychodWewnetrzny</c>, BO 12354) via the
/// <c>InsERT.Moria.Dokumenty.Logistyka.IPrzychodyWewnetrzne</c> facade. Sibling of
/// <see cref="SferaDokumentySprzedazyService"/>: runs synchronously against a passed-in
/// <see cref="SferaSession"/> (no Task.Run/lock — <see cref="SferaWriteQueue"/>
/// serializes) and reuses the shared <see cref="SferaObjectAccessor"/> for every
/// set/get/flag/validation-error path.
/// <para>
/// SKELETON: the body is filled on Windows against the live Sfera API once the
/// <c>/api/diag/pw-bo</c> discovery dump confirms (1) the PW factory method name on the
/// facade, (2) the writable <c>Magazyn</c>/<c>Partia</c>/<c>Ilosc</c> members on a
/// <c>PozycjaDokumentu</c>, (3) the warehouse-date member (<c>DataMagazynowa</c> vs
/// <c>DataPrzyjecia</c>), (4) that <c>Zapisz()</c> succeeds with no
/// <c>PodmiotyDokumentu</c>, and (5) the <c>Id</c> read-back ordering (whether
/// <c>Id &lt;= 0</c> provably means "no document persisted"). Do NOT guess API names.
/// </para>
/// </summary>
public sealed class SferaPrzychodWewnetrznyService
{
    private readonly ILogger<SferaPrzychodWewnetrznyService> _log;
    private readonly SferaObjectAccessor _acc;

    public SferaPrzychodWewnetrznyService(ILogger<SferaPrzychodWewnetrznyService> log)
    {
        _log = log;
        _acc = new SferaObjectAccessor(log);
    }

    /// <summary>
    /// Create and persist a PW for <paramref name="input"/>, returning the created
    /// document's id + full number. On a clean failure the contract is to throw a
    /// <see cref="BridgeException"/> (classified by the caller). The created
    /// <c>Id &lt;= 0</c> handling is driven by the Phase A ordering finding and is
    /// asserted here before returning (see class remarks).
    /// </summary>
    public (int Id, string Numer) UtworzPrzychod(SferaSession sfera, SferaReceiptInput input)
    {
        ArgumentNullException.ThrowIfNull(sfera);
        ArgumentNullException.ThrowIfNull(input);

        // TODO(Faza-Windows): implement against the live Sfera API using the captured
        // /api/diag/pw-bo shape. Steps mirror SferaDokumentySprzedazyService.Create:
        //   1. validate the product exists (Asortymenty SELECT);
        //   2. create the PW BO via the confirmed IPrzychodyWewnetrzne factory;
        //   3. add the position via the proven Pozycje.Dodaj(symbol, ilosc);
        //   4. set target Magazyn + Partia/batch + DataMagazynowa per diag;
        //   5. leave PodmiotyDokumentu unset (no supplier);
        //   6. Przelicz/Waliduj/Zapisz; read back Id + full number;
        //   7. assert Id > 0 (classify Id <= 0 per the Phase A ordering finding).
        throw new NotImplementedException("PW creation — verify on Windows against live Sfera (see /api/diag/pw-bo).");
    }
}
