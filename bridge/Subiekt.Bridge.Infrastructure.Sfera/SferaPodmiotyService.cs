using Microsoft.Extensions.Logging;

namespace Subiekt.Bridge.Infrastructure.Sfera;

/// <summary>
/// Moved verbatim (move-and-wrap) from the legacy <c>SferaApi.Services.PodmiotyService</c>.
/// Business logic is unchanged; the only structural changes are:
/// <list type="bullet">
/// <item>runs SYNCHRONOUSLY against a <see cref="SferaSession"/> passed in by the
/// caller — the <c>Task.Run</c> + <c>lock(SyncRoot)</c> wrapper is gone because
/// <see cref="SferaWriteQueue"/> now serializes all mutations on one worker;</item>
/// <item>the duplicated reflection helpers (SetEntity/GetEntity/InvokeIfExists/
/// CollectValidationErrors/ExtractEntityErrors/NavTo/DumpShape) are replaced by the
/// shared <see cref="SferaObjectAccessor"/>;</item>
/// <item>input is the Infrastructure-local <see cref="SferaCustomerInput"/> instead of
/// the Api DTO.</item>
/// </list>
/// Returns the assigned (Id, Numer). Throws on rejection (the adapter classifies it).
/// </summary>
public sealed class SferaPodmiotyService
{
    private readonly ILogger<SferaPodmiotyService> _log;
    private readonly SferaObjectAccessor _acc;

    public SferaPodmiotyService(ILogger<SferaPodmiotyService> log)
    {
        _log = log;
        _acc = new SferaObjectAccessor(log);
    }

    public (int Id, string Numer) UpsertFirma(SferaSession sfera, SferaCustomerInput dto)
        => CreateAndSave(sfera, "UtworzFirme", dto);

    public (int Id, string Numer) UpsertOsoba(SferaSession sfera, SferaCustomerInput dto)
        => CreateAndSave(sfera, "UtworzOsobe", dto);

    private (int Id, string Numer) CreateAndSave(SferaSession sfera, string factoryMethod, SferaCustomerInput dto)
    {
        var uchwyt = sfera.Uchwyt;
        var conn = uchwyt.PodajPolaczenie();
        if (conn.State != System.Data.ConnectionState.Open)
            conn.Open();

        // Step 1: Idempotency — if a kontrahent with this NIP exists, return it.
        if (!string.IsNullOrEmpty(dto.NIP))
        {
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = @"
                SELECT Id, Sygnatura_PelnaSygnatura
                FROM ModelDanychContainer.Podmioty
                WHERE NIP = @nip AND Kontrahent = 1";
            checkCmd.Parameters.AddWithValue("@nip", dto.NIP);
            using var r = checkCmd.ExecuteReader();
            if (r.Read())
            {
                var existingId = r.GetInt32(0);
                var existingNumer = r.IsDBNull(1) ? "" : r.GetString(1);
                _log.LogInformation("Idempotency hit: NIP {nip} already exists id={id} numer={numer}",
                    dto.NIP, existingId, existingNumer);
                return (existingId, existingNumer);
            }
        }

        // Step 2: Get IPodmioty facade and create the business object (BO).
        var iPodmiotyType = SferaReflection.RequireType("InsERT.Moria.Klienci.IPodmioty, InsERT.Moria.Klienci");
        var iPodmioty = uchwyt.PodajObiektTypu(iPodmiotyType);

        var factory = iPodmiotyType.GetMethod(factoryMethod, SferaObjectAccessor.Flags)
            ?? throw new InvalidOperationException($"IPodmioty.{factoryMethod}() not found");

        var bo = factory.Invoke(iPodmioty, null)
            ?? throw new InvalidOperationException($"{factoryMethod}() returned null");

        var boType = bo.GetType();

        // Step 3: The real fields live on the nested `Dane` entity (Podmiot),
        // not on the BO itself. Grab it and set the data there.
        var dane = boType.GetProperty("Dane", SferaObjectAccessor.Flags)?.GetValue(bo)
            ?? throw new InvalidOperationException("PodmiotBO.Dane is null");
        var daneType = dane.GetType();

        // Osoba fizyczna keys off Imie+Nazwisko (no NazwaSkrocona to set);
        // a firma uses NazwaSkrocona. Split "Imię Nazwisko" for a person.
        var isOsoba = factoryMethod.Equals("UtworzOsobe", StringComparison.OrdinalIgnoreCase);
        if (isOsoba)
        {
            var parts = (dto.NazwaSkrocona ?? "").Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var imie = parts.Length > 0 ? parts[0] : "Klient";
            var nazwisko = parts.Length > 1 ? parts[1] : "Detaliczny";
            var osoba = daneType.GetProperty("Osoba", SferaObjectAccessor.Flags)?.GetValue(dane);
            if (osoba != null)
            {
                var ot = osoba.GetType();
                _acc.SetProperty(osoba, ot, "Imie", imie);
                _acc.SetProperty(osoba, ot, "Nazwisko", nazwisko);
            }
            else _log.LogWarning("Podmiot.Osoba is null — nie mogę ustawić Imie/Nazwisko");
            _acc.SetProperty(dane, daneType, "NazwaSkrocona", string.IsNullOrWhiteSpace(dto.NazwaSkrocona) ? $"{imie} {nazwisko}" : dto.NazwaSkrocona);
        }
        else
        {
            _acc.SetProperty(dane, daneType, "NazwaSkrocona", dto.NazwaSkrocona);
        }
        _acc.SetProperty(dane, daneType, "Kontrahent", true);
        _acc.SetProperty(dane, daneType, "Aktywny", dto.Aktywny);
        if (!string.IsNullOrEmpty(dto.NIP))
            _acc.SetProperty(dane, daneType, "NIP", dto.NIP);
        if (!string.IsNullOrEmpty(dto.Telefon))
            _acc.SetProperty(dane, daneType, "Telefon", dto.Telefon);

        // Step 3b: Structured address — created via PodmiotBO.DodajAdres(TypAdresu).
        if (dto.Address != null)
            SetAddress(sfera, bo, boType, dane, daneType, dto.Address);

        // Step 4: Assign the symbol/number (auto-numbering), best-effort.
        _acc.InvokeIfExists(bo, boType, "AutoSymbol");
        _acc.InvokeIfExists(bo, boType, "NadajNumer");

        // Step 5: Persist. Zapisz() returns bool — false means save rejected.
        var zapisz = boType.GetMethod("Zapisz", SferaObjectAccessor.Flags, Type.EmptyTypes)
            ?? throw new InvalidOperationException("PodmiotBO.Zapisz() not found");
        var saveResult = zapisz.Invoke(bo, null);
        var saved = saveResult is bool b && b;
        _log.LogInformation("Zapisz() returned {result}", saveResult);

        if (!saved)
        {
            var reason = _acc.CollectValidationErrors(bo, boType);
            throw new InvalidOperationException(
                $"Sfera rejected save (Zapisz returned false). {reason}");
        }

        // Step 6: Read back the assigned Id and Sygnatura from the entity.
        var createdId = _acc.GetProperty(dane, daneType, "Id") is int idVal ? idVal : -1;
        var createdNumer = _acc.GetProperty(dane, daneType, "Sygnatura") as string ?? "";

        // The full sygnatura is often not materialized on the in-memory entity
        // right after save — re-read it from the DB by Id.
        if (string.IsNullOrEmpty(createdNumer) && createdId > 0)
        {
            using var numCmd = conn.CreateCommand();
            numCmd.CommandText = "SELECT Sygnatura_PelnaSygnatura FROM ModelDanychContainer.Podmioty WHERE Id = @id";
            numCmd.Parameters.AddWithValue("@id", createdId);
            var numResult = numCmd.ExecuteScalar();
            if (numResult is string s) createdNumer = s;
        }

        _log.LogInformation("{factory} saved: id={id} numer={numer} nazwa={nazwa}",
            factoryMethod, createdId, createdNumer, dto.NazwaSkrocona);
        return (createdId, createdNumer);
    }

    public (int Id, string Numer)? WyszukajWgNIP(SferaSession sfera, string nip)
    {
        var conn = sfera.Uchwyt.PodajPolaczenie();
        if (conn.State != System.Data.ConnectionState.Open)
            conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1 Id, Sygnatura_PelnaSygnatura
            FROM ModelDanychContainer.Podmioty
            WHERE NIP = @nip AND Kontrahent = 1";
        cmd.Parameters.AddWithValue("@nip", nip);

        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return null;

        var id = r.GetInt32(0);
        var numer = r.IsDBNull(1) ? "" : r.GetString(1);
        return (id, numer);
    }

    // Create + populate the Podmiot's primary address. A new BO has no address,
    // so we call PodmiotBO.DodajAdres(TypAdresu) which returns the AdresPodmiotu
    // entity to fill in. (Behaviour identical to the legacy SetAddress.)
    private void SetAddress(SferaSession sfera, object bo, Type boType, object dane, Type daneType, SferaAddressInput addr)
    {
        var dodajAdres = boType.GetMethod("DodajAdres", SferaObjectAccessor.Flags);
        if (dodajAdres == null)
        {
            _log.LogWarning("PodmiotBO.DodajAdres not found — address not set");
            return;
        }

        var typAdresuType = dodajAdres.GetParameters().FirstOrDefault()?.ParameterType;
        if (typAdresuType == null) { _log.LogWarning("DodajAdres has no parameter — address not set"); return; }

        object? typVal = null;

        if (typAdresuType.IsEnum)
        {
            var names = Enum.GetNames(typAdresuType);
            var chosen = names.FirstOrDefault(n => n.Equals("Glowny", StringComparison.OrdinalIgnoreCase))
                         ?? names.FirstOrDefault(n => n.Equals("Podstawowy", StringComparison.OrdinalIgnoreCase))
                         ?? names.FirstOrDefault();
            if (chosen != null) typVal = Enum.Parse(typAdresuType, chosen);
        }
        else
        {
            try
            {
                var facadeType = SferaReflection.RequireType("InsERT.Moria.Klienci.ITypyAdresu, InsERT.Moria.Klienci");
                var facade = sfera.Uchwyt.PodajObiektTypu(facadeType);
                var znajdz = facadeType.GetMethod("Znajdz", SferaObjectAccessor.Flags, new[] { typeof(string) });
                var found = znajdz?.Invoke(facade, new object[] { "główny" });
                if (found != null)
                {
                    if (typAdresuType.IsInstanceOfType(found)) typVal = found;
                    else
                    {
                        var inner = found.GetType().GetProperty("Dane", SferaObjectAccessor.Flags)?.GetValue(found);
                        if (inner != null && typAdresuType.IsInstanceOfType(inner)) typVal = inner;
                    }
                }
            }
            catch (Exception ex) { _log.LogWarning("ITypyAdresu.Znajdz('główny') failed: {e}", ex.Message); }
        }

        if (typVal == null)
        {
            _log.LogWarning("Could not resolve TypAdresu 'główny' for {t} — address not set", typAdresuType.Name);
            return;
        }

        object? adresPodmiotu;
        try { adresPodmiotu = dodajAdres.Invoke(bo, new[] { typVal }); }
        catch (Exception ex) { _log.LogError(ex, "DodajAdres failed: {e}", ex.Message); return; }
        if (adresPodmiotu == null) { _log.LogWarning("DodajAdres returned null — address not set"); return; }

        _acc.DumpShape("DodajAdres result", adresPodmiotu);

        var adres = _acc.NavTo(adresPodmiotu, "Adres") ?? adresPodmiotu;
        var szczegoly = _acc.NavTo(adres, "Szczegoly") ?? adres;
        if (!ReferenceEquals(szczegoly, adres)) _acc.DumpShape("Adres.Szczegoly", szczegoly);
        else _acc.DumpShape("Adres", adres);

        var szType = szczegoly.GetType();
        if (!string.IsNullOrWhiteSpace(addr.Ulica)) _acc.SetProperty(szczegoly, szType, "Ulica", addr.Ulica);
        if (!string.IsNullOrWhiteSpace(addr.NrDomu)) _acc.SetProperty(szczegoly, szType, "NrDomu", addr.NrDomu);
        if (!string.IsNullOrWhiteSpace(addr.NrLokalu)) _acc.SetProperty(szczegoly, szType, "NrLokalu", addr.NrLokalu);
        if (!string.IsNullOrWhiteSpace(addr.KodPocztowy)) _acc.SetProperty(szczegoly, szType, "KodPocztowy", addr.KodPocztowy);
        if (!string.IsNullOrWhiteSpace(addr.Miejscowosc)) _acc.SetProperty(szczegoly, szType, "Miejscowosc", addr.Miejscowosc);
        if (!string.IsNullOrWhiteSpace(addr.Poczta)) _acc.SetProperty(szczegoly, szType, "Poczta", addr.Poczta);

        var adresT = adres.GetType();
        var linia = string.Join(" ", new[] { addr.Ulica, addr.NrDomu + (string.IsNullOrWhiteSpace(addr.NrLokalu) ? "" : "/" + addr.NrLokalu) }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (!string.IsNullOrWhiteSpace(linia)) _acc.SetProperty(adres, adresT, "Linia1", linia);

        var apProp = daneType.GetProperty("AdresPodstawowy", SferaObjectAccessor.Flags);
        if (apProp?.CanWrite == true && apProp.PropertyType.IsInstanceOfType(adresPodmiotu))
        {
            try { apProp.SetValue(dane, adresPodmiotu); } catch (Exception ex) { _log.LogWarning("AdresPodstawowy assign failed: {e}", ex.Message); }
        }

        _log.LogInformation("Address created via DodajAdres: {city} {postal}", addr.Miejscowosc, addr.KodPocztowy);
    }
}
