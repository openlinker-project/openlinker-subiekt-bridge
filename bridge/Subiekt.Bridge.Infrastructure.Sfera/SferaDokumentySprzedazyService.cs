using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Subiekt.Bridge.Infrastructure.Sfera;

/// <summary>
/// Moved verbatim (move-and-wrap) from the legacy
/// <c>SferaApi.Services.DokumentySprzedazyService</c>. Business logic is unchanged;
/// structural changes only: runs synchronously against a passed-in
/// <see cref="SferaSession"/> (no Task.Run/lock — <see cref="SferaWriteQueue"/>
/// serializes), uses the shared <see cref="SferaObjectAccessor"/> instead of the
/// duplicated helpers, and takes <see cref="SferaInvoiceInput"/> instead of the Api DTO.
/// <para>
/// Issue #1 adds an EXPLICIT payment-selection path (step 6b): when
/// <see cref="SferaPaymentInput"/> is present, the caller-chosen cash/transfer form
/// and (for transfer) seller bank account replace the config-driven default calls.
/// Mechanism verified live — see <c>docs/spikes/bank-account-probe-findings.md</c> s.3.
/// </para>
/// </summary>
public sealed class SferaDokumentySprzedazyService
{
    private readonly ILogger<SferaDokumentySprzedazyService> _log;
    private readonly SferaObjectAccessor _acc;
    private readonly SferaOptions _opt;

    public SferaDokumentySprzedazyService(ILogger<SferaDokumentySprzedazyService> log, IOptions<SferaOptions> options)
    {
        _log = log;
        _acc = new SferaObjectAccessor(log);
        _opt = options.Value;
    }

    public (int Id, string Numer) UtworzFaktura(SferaSession sfera, SferaInvoiceInput dto)
        => Create(sfera, "UtworzFaktureSprzedazy", dto);

    public (int Id, string Numer) UtworzParagon(SferaSession sfera, SferaInvoiceInput dto)
        => Create(sfera, "UtworzParagon", dto);

    private (int Id, string Numer) Create(SferaSession sfera, string factoryMethod, SferaInvoiceInput dto)
    {
        var uchwyt = sfera.Uchwyt;
        var conn = uchwyt.PodajPolaczenie();
        if (conn.State != System.Data.ConnectionState.Open)
            conn.Open();

        // 1. Validate kontrahent exists.
        using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "SELECT COUNT(*) FROM ModelDanychContainer.Podmioty WHERE Id = @id";
            checkCmd.Parameters.AddWithValue("@id", dto.KontrahentId);
            if ((int)checkCmd.ExecuteScalar() == 0)
                throw new ArgumentException($"Kontrahent ID {dto.KontrahentId} not found");
        }

        // 2. Create the document BO.
        var iDokumentyType = SferaReflection.RequireType("InsERT.Moria.Dokumenty.Logistyka.IDokumentySprzedazy, InsERT.Moria.Dokumenty.Logistyka");
        var facade = uchwyt.PodajObiektTypu(iDokumentyType);
        var factory = iDokumentyType.GetMethod(factoryMethod, SferaObjectAccessor.Flags)
            ?? throw new InvalidOperationException($"IDokumentySprzedazy.{factoryMethod}() not found");
        var bo = factory.Invoke(facade, null)
            ?? throw new InvalidOperationException($"{factoryMethod}() returned null");
        var boType = bo.GetType();

        var dane = boType.GetProperty("Dane", SferaObjectAccessor.Flags)?.GetValue(bo)
            ?? throw new InvalidOperationException("Document BO.Dane is null");
        var daneType = dane.GetType();

        // Bypass soft blocks that stop Zapisz() returning false in a POC/demo.
        _acc.SetBoolFlag(bo, boType, "IgnorujBlokade", true);
        _acc.SetBoolFlag(bo, boType, "WylaczBlokowanieStanowPrzezRezerwacjeIlosciowa", true);
        _acc.SetBoolFlag(bo, boType, "NieSprawdzajRealizacji", true);

        // 3. Set the buyer using the document BO's OWN method, so the Podmiot is
        //    resolved inside the document's UnitOfWork.
        var ustawNabywce = boType.GetMethod("UstawNabywceWedlugId", SferaObjectAccessor.Flags, new[] { typeof(int) })
            ?? throw new InvalidOperationException("Document BO.UstawNabywceWedlugId(int) not found");
        ustawNabywce.Invoke(bo, new object[] { dto.KontrahentId });
        _log.LogInformation("Buyer set via UstawNabywceWedlugId({id})", dto.KontrahentId);

        // 4. Dates. Both fiscal dates are computed by the Domain aggregate
        //    (SalesDocument.ComputeFiscalDates) and passed in resolved: DataSprzedazy is the
        //    sale/VAT month (may be in the past); DataWydaniaWystawienia is the dispatch/entry
        //    date. The service no longer derives them inline.
        _acc.SetProperty(dane, daneType, "DataSprzedazy", dto.DataSprzedazy);
        _acc.SetProperty(dane, daneType, "DataWydaniaWystawienia", dto.DataWydania);

        // 5. Add positions. Catalogue product -> Dodaj(symbol); product NOT in Subiekt
        //    -> DodajUslugeJednorazowa (one-time line).
        var dodaj = boType.GetMethod("Dodaj", SferaObjectAccessor.Flags, new[] { typeof(string) })
            ?? throw new InvalidOperationException("Document BO.Dodaj(string) not found");
        var dodajUsluga = boType.GetMethod("DodajUslugeJednorazowa", SferaObjectAccessor.Flags, new[] { typeof(string), typeof(decimal) });

        // Lines arrive already discount-folded by the adapter (SferaInvoiceIssuer applies
        // Domain.SalesDocument.FoldDiscounts), so no negative-price positions remain and the
        // per-line gross prices are final. The defensive skip below guards that contract.
        var allLines = dto.Lines ?? Array.Empty<SferaInvoiceLineInput>();

        foreach (var line in allLines)
        {
            if (line.CenaBrutto < 0) continue;   // defensive: discounts are folded upstream

            bool exists;
            using (var ce = conn.CreateCommand())
            {
                ce.CommandText = "SELECT COUNT(*) FROM ModelDanychContainer.Asortymenty WHERE Symbol = @s AND IsInRecycleBin = 0";
                ce.Parameters.AddWithValue("@s", line.TowarSymbol);
                exists = (int)ce.ExecuteScalar() > 0;
            }

            object? poz;
            if (exists)
            {
                poz = dodaj.Invoke(bo, new object[] { line.TowarSymbol })
                    ?? throw new InvalidOperationException($"Could not add position for product '{line.TowarSymbol}'");
                _acc.SetProperty(poz, poz.GetType(), "Ilosc", line.Ilosc);
            }
            else
            {
                if (dodajUsluga == null)
                    throw new InvalidOperationException("DodajUslugeJednorazowa(string,decimal) not found");
                var nazwa = !string.IsNullOrWhiteSpace(line.Name) ? line.Name! : line.TowarSymbol;
                poz = dodajUsluga.Invoke(bo, new object[] { nazwa, line.Ilosc })
                    ?? throw new InvalidOperationException($"Could not add one-time service '{nazwa}'");
                _log.LogInformation("Product '{s}' not in Subiekt — added one-time service '{n}' x {q}", line.TowarSymbol, nazwa, line.Ilosc);
            }

            var pozType = poz.GetType();

            // Honor the VAT rate from the request.
            var vatId = LookupStawkaVatId(conn, line.StawkaVAT);
            if (vatId != null)
                _acc.SetProperty(poz, pozType, "StawkaVatId", vatId.Value);
            else
                _log.LogWarning("VAT rate '{r}' not found in StawkiVat — keeping product default", line.StawkaVAT);

            // Honor the (already discount-folded) gross unit price.
            if (line.CenaBrutto > 0)
            {
                var effCena = line.CenaBrutto;
                var cenaProp = pozType.GetProperty("Cena", SferaObjectAccessor.Flags);
                var cena = cenaProp?.GetValue(poz);
                if (cena != null)
                {
                    var cenaT = cena.GetType();
                    _acc.SetProperty(cena, cenaT, "BruttoPrzedRabatem", effCena);
                    _acc.SetProperty(cena, cenaT, "BruttoPoRabacie", effCena);
                    if (cenaProp!.CanWrite) cenaProp.SetValue(poz, cena); // set back in case Cena is a copy
                }
                _acc.SetProperty(poz, pozType, "CenaRecznieEdytowana", true);
            }

            _log.LogDebug("Added line {symbol} x {ilosc} @ {cena} brutto, VAT {vat}",
                line.TowarSymbol, line.Ilosc, line.CenaBrutto, line.StawkaVAT);
        }

        // 6. Recompute netto/VAT split from the manual gross prices.
        _acc.InvokeIfExists(bo, boType, "Przelicz");

        // 6b. Payments. An EXPLICIT selection (issue #1) replaces the config-driven
        //     defaults; otherwise today's default calls fire verbatim (no regression).
        if (dto.Payment is { } payment)
        {
            ApplyExplicitPayment(conn, bo, boType, dane, daneType, payment);
        }
        else
        {
            _acc.InvokeIfExists(bo, boType, "DodajPlatnosciDomyslne");
            _acc.InvokeIfExists(bo, boType, "DodajDomyslnaPlatnoscNatychmiastowaNaKwoteDokumentu");
        }

        // 6c. Allow the invoice out even when stock is insufficient.
        _acc.InvokeIfExists(bo, boType, "IgnorujBlokadeRealizacjiPozycji");

        _acc.InvokeIfExists(bo, boType, "AutoSymbol");
        _acc.InvokeIfExists(bo, boType, "NadajNumer");

        // 7. Validate explicitly — Waliduj() throws a descriptive Sfera exception.
        var waliduj = boType.GetMethod("Waliduj", SferaObjectAccessor.Flags, Type.EmptyTypes);
        if (waliduj != null)
        {
            try
            {
                waliduj.Invoke(bo, null);
                _log.LogInformation("Waliduj() passed");
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                _log.LogError("Waliduj() failed: {msg}", tie.InnerException.Message);
                throw new InvalidOperationException("Sfera validation failed: " + tie.InnerException.Message);
            }
        }

        // Diagnostics before save: dump State, MoznaZapisac, FK fields and warning
        // collections (kept at Debug; PII/internals never at Information).
        try
        {
            _log.LogDebug("State = {v}", boType.GetProperty("State", SferaObjectAccessor.Flags)?.GetValue(bo));
            _log.LogDebug("MoznaZapisac = {v}", boType.GetProperty("MoznaZapisac", SferaObjectAccessor.Flags)?.GetValue(bo));

            foreach (var f in new[] { "MagazynId", "MojaFirmaId", "MiejsceSprzedazyId", "MiejsceWprowadzeniaId",
                                      "NabywcaSprzedawcaId", "PodmiotId", "PlatnikId", "FormaPlatnosciId",
                                      "KonfiguracjaId", "StatusDokumentuId", "KategoriaDokumentuId",
                                      "Symbol", "NumerWewnetrzny_PelnaSygnatura", "WystawilaOsobaId" })
            {
                var pr = daneType.GetProperty(f, SferaObjectAccessor.Flags);
                if (pr != null) _log.LogDebug("Dane.{f} = {v}", f, pr.GetValue(dane) ?? "NULL");
            }

            var inv = boType.GetProperty("InvalidData", SferaObjectAccessor.Flags)?.GetValue(bo);
            if (inv is System.Collections.IEnumerable invEnum)
            {
                var fieldErrs = new List<string>();
                foreach (var ent in invEnum) if (ent != null) _acc.ExtractEntityErrors(ent, fieldErrs);
                if (fieldErrs.Count > 0) _log.LogWarning("Field errors: {e}", string.Join(" || ", fieldErrs.Distinct()));
                else _log.LogWarning("InvalidData present but no field-level messages exposed");
            }

            foreach (var p in boType.GetProperties(SferaObjectAccessor.Flags))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                var n = p.Name;
                if (!(n.Contains("strzez") || n.Contains("Ostrzez") || n.Contains("Komunikat") || n.Contains("Blad") || n.Contains("Invalid")))
                    continue;
                object? val = null;
                try { val = p.GetValue(bo); } catch { continue; }
                if (val is System.Collections.IEnumerable en and not string)
                {
                    var items = new List<string>();
                    foreach (var it in en) if (it != null) items.Add(it.ToString() ?? "");
                    if (items.Count > 0) _log.LogWarning("{prop} ({n}): {items}", n, items.Count, string.Join(" || ", items));
                }
                else if (val != null)
                {
                    _log.LogDebug("{prop} = {v}", n, val);
                }
            }
        }
        catch (Exception ex) { _log.LogWarning("pre-save diag failed: {e}", ex.Message); }

        // 8. Persist.
        var zapisz = boType.GetMethod("Zapisz", SferaObjectAccessor.Flags, Type.EmptyTypes)
            ?? throw new InvalidOperationException("Document BO.Zapisz() not found");
        var saveResult = zapisz.Invoke(bo, null);
        var saved = saveResult is bool b && b;
        _log.LogInformation("Zapisz() returned {result}", saveResult);
        if (!saved)
            throw new InvalidOperationException($"Sfera rejected save (Zapisz=false). {_acc.CollectValidationErrors(bo, boType, includeDocumentLevel: true, includeStateHint: true)}");

        // 8b. Read back Id + full document number (NumerWewnetrzny_PelnaSygnatura).
        var id = _acc.GetProperty(dane, daneType, "Id") is int idv ? idv : -1;
        var numer = _acc.GetProperty(dane, daneType, "Symbol") as string ?? "";

        if (id > 0)
        {
            using var nc = conn.CreateCommand();
            nc.CommandText = "SELECT NumerWewnetrzny_PelnaSygnatura FROM ModelDanychContainer.Dokumenty WHERE Id = @id";
            nc.Parameters.AddWithValue("@id", id);
            if (nc.ExecuteScalar() is string s && !string.IsNullOrWhiteSpace(s)) numer = s;
        }

        _log.LogInformation("{factory} saved: id={id} numer={numer}", factoryMethod, id, numer);
        return (id, numer);
    }

    // Apply an EXPLICIT payment selection (issue #1). Verified live mechanism
    // (docs/spikes/bank-account-probe-findings.md s.3):
    //   1. resolve the configured FormaPlatnosci row by name (SQL pre-check),
    //   2. materialize the entity INSIDE the document's unit of work via FK fixup,
    //   3. add the payment row through the IPlatnosciNaDokumencie interface
    //      (explicitly implemented on the BO — lookup must go through the interface),
    //   4. set Dane.FormaPlatnosci so the header carries the chosen form,
    //   5. transfer only: write the seller-account snapshot on the document's
    //      pre-attached RachunekBankowyDokumentu ({Nazwa, Numer} copy, not an FK).
    // DodajPlatnosciDomyslne is deliberately NOT called here — it derives from
    // configuration and ignores a pre-set Dane.FormaPlatnosci.
    private void ApplyExplicitPayment(
        Microsoft.Data.SqlClient.SqlConnection conn,
        object bo, Type boType, object dane, Type daneType,
        SferaPaymentInput payment)
    {
        var isCash = string.Equals(payment.Method, "cash", StringComparison.OrdinalIgnoreCase);
        var formName = isCash ? _opt.CashPaymentFormName : _opt.TransferPaymentFormName;

        // 1. Resolve the payment-form row (operator-configurable name, active only).
        int fpId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT TOP 1 Id FROM ModelDanychContainer.FormyPlatnosci
                                WHERE LOWER(Nazwa) = LOWER(@n) AND Aktywna = 1";
            cmd.Parameters.AddWithValue("@n", formName);
            fpId = cmd.ExecuteScalar() is int id
                ? id
                : throw new ArgumentException(
                    $"Forma płatności '{formName}' nie istnieje lub jest nieaktywna (konfiguracja Sfera:{(isCash ? "CashPaymentFormName" : "TransferPaymentFormName")}).");
        }

        // 1b. Transfer: pre-check the selected seller account (exists, owned by ANY
        //     seller Podmiot — issue #3 — active, currency matches the document).
        string? accName = null, accNumber = null;
        if (!isCash)
        {
            var accountId = payment.BankAccountId
                ?? throw new ArgumentException("Płatność 'transfer' wymaga bankAccountId.");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                ;WITH SellerPodmioty AS (
                    SELECT Id FROM ModelDanychContainer.Podmioty WHERE Typ = 2 AND Podtyp = 11
                )
                SELECT cgf.Nazwa, rb.Numer, w.Symbol, rb.Aktywny, rb.Wlasciciel_Id,
                       (SELECT COUNT(*) FROM SellerPodmioty) AS SellerCount
                FROM ModelDanychContainer.CentraGromadzeniaFinansow_RachunekBankowy rb
                JOIN ModelDanychContainer.CentraGromadzeniaFinansow cgf ON cgf.Id = rb.Id
                LEFT JOIN ModelDanychContainer.Waluty w ON w.Id = rb.Waluta_Id
                WHERE rb.Id = @id
                  AND rb.Wlasciciel_Id IN (SELECT Id FROM SellerPodmioty)";
            cmd.Parameters.AddWithValue("@id", accountId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new ArgumentException($"Rachunek bankowy {accountId} nie istnieje lub nie należy do sprzedawcy.");
            accName = reader.IsDBNull(0) ? "" : reader.GetString(0);
            accNumber = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var accCurrency = reader.IsDBNull(2) ? null : reader.GetString(2);
            var active = reader.GetBoolean(3);
            var ownerPodmiotId = reader.GetInt32(4);
            var sellerPodmiotCount = reader.GetInt32(5);
            if (!active)
                throw new ArgumentException($"Rachunek bankowy {accountId} jest nieaktywny.");
            if (accCurrency is not null
                && !string.Equals(accCurrency, payment.Currency, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"Rachunek bankowy {accountId} jest w walucie {accCurrency}, a dokument w {payment.Currency}.");

            // Issue #3 interim guard: the pre-check above accepts an account owned by ANY
            // seller Podmiot, but it CANNOT yet verify the owner matches the Podmiot this
            // document is issued under - whether Dane.MojaFirmaId maps 1:1 onto a
            // Podmioty row (vs. a separate MojaFirma entity) is exactly the pre-probe
            // unknown that `BankAccountProbe podmioty` exists to answer, so a hard check
            // would risk false rejections on every install. Until the Oddzial/Platnik
            // selector lands (open work on issue #3), the CALLER must ensure
            // bankAccountId belongs to the intended payer; on multi-payer installs we log
            // an informational audit trail so a cross-payer stamp is traceable instead of
            // silent. This fires on EVERY transfer issuance on such installs, including
            // correct picks, so it is LogInformation (not LogWarning) - it records expected
            // behavior on multi-payer installs, not an error condition.
            if (sellerPodmiotCount > 1)
                _log.LogInformation(
                    "Multi-payer install ({count} seller Podmioty): bank account {accountId} owned by Podmiot {ownerId} accepted WITHOUT validating it matches the document's payer (issue #3) - caller must ensure the account belongs to the intended payer",
                    sellerPodmiotCount, accountId, ownerPodmiotId);
        }

        // 2. Materialize the FormaPlatnosci entity in the document's unit of work.
        var fpIdProp = daneType.GetProperty("FormaPlatnosciId", SferaObjectAccessor.Flags)
            ?? throw new InvalidOperationException("Dane.FormaPlatnosciId not found");
        fpIdProp.SetValue(dane, fpId);
        var fp = daneType.GetProperty("FormaPlatnosci", SferaObjectAccessor.Flags)?.GetValue(dane);
        if (fp is null || !Equals(_acc.GetProperty(fp, fp.GetType(), "Id"), fpId))
            throw new InvalidOperationException($"Nie udało się rozwiązać encji FormaPlatnosci {fpId} w kontekście dokumentu.");

        // 3. Add the payment row via IPlatnosciNaDokumencie (interface-typed lookup).
        var addName = isCash ? "DodajPlatnoscNatychmiastowa" : "DodajPlatnoscOdroczona";
        var add = ResolveDodajPlatnoscMethod(addName);
        try
        {
            add.Invoke(bo, new[] { fp });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw new InvalidOperationException($"{addName} failed: " + tie.InnerException.Message);
        }

        // 4-5 run AFTER the payment row (step 3) has already been added to `bo`. Sfera's
        // API exposes no "remove payment" call to roll that row back, so if either step
        // below throws we cannot repair `bo` — we can only make sure it is never Zapisz()'d
        // (Create() always mints a brand-new `bo` per call, so a caller-level retry starts
        // from a clean business object) and that the failure is loud in the logs.
        try
        {
            // 4. Re-assert the header's payment form (the add above may not set it).
            _acc.SetProperty(dane, daneType, "FormaPlatnosci", fp);

            // 5. Transfer: stamp the chosen seller account on the document snapshot.
            if (!isCash)
            {
                var rbdProp = daneType.GetProperty("RachunkiBankowe", SferaObjectAccessor.Flags)
                    ?? throw new InvalidOperationException("Dane.RachunkiBankowe not found");
                var rbd = rbdProp.GetValue(dane);
                if (rbd is null)
                {
                    var rbdType = SferaReflection.RequireType("InsERT.Moria.ModelDanych.RachunekBankowyDokumentu, InsERT.Moria.ModelDanych");
                    rbd = Activator.CreateInstance(rbdType)!;
                    if (rbdProp.CanWrite) rbdProp.SetValue(dane, rbd);
                }
                var mfProp = rbd.GetType().GetProperty("RachunekBankowyMojejFirmy", SferaObjectAccessor.Flags)
                    ?? throw new InvalidOperationException("RachunekBankowyDokumentu.RachunekBankowyMojejFirmy not found");
                var snapshot = mfProp.GetValue(rbd);
                if (snapshot is null)
                {
                    var snapType = SferaReflection.RequireType("InsERT.Moria.ModelDanych.DaneRachunkuBankowego, InsERT.Moria.ModelDanych");
                    snapshot = Activator.CreateInstance(snapType)!;
                    if (mfProp.CanWrite) mfProp.SetValue(rbd, snapshot);
                }
                _acc.SetProperty(snapshot, snapshot.GetType(), "Nazwa", accName);
                _acc.SetProperty(snapshot, snapshot.GetType(), "Numer", accNumber);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Payment row was added to the document ({addName}) but the header/bank-account " +
                "snapshot step failed afterward — this business object is left in a half-applied " +
                "state and will be discarded WITHOUT calling Zapisz() (never persisted). The caller " +
                "must retry the whole Create() call, which always fetches a fresh BO: {msg}",
                addName, ex.Message);
            throw;
        }

        _log.LogInformation("Explicit payment applied: {method} (forma '{forma}'{acct})",
            payment.Method, formName, isCash ? "" : $", rachunek {payment.BankAccountId}");
    }

    // MethodInfo lookup for IPlatnosciNaDokumencie.DodajPlatnoscNatychmiastowa/DodajPlatnoscOdroczona
    // is invariant per process (same interface, same overload shape) — cache it instead of
    // re-running GetMethods()/LINQ on every invoice creation.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, MethodInfo> _dodajPlatnoscMethodCache = new();

    private static MethodInfo ResolveDodajPlatnoscMethod(string addName)
        => _dodajPlatnoscMethodCache.GetOrAdd(addName, static name =>
        {
            var iPlatnosci = SferaReflection.RequireType("InsERT.Moria.Dokumenty.Logistyka.IPlatnosciNaDokumencie, InsERT.Moria.API");
            var fpEntityType = SferaReflection.RequireType("InsERT.Moria.ModelDanych.FormaPlatnosci, InsERT.Moria.ModelDanych");
            return iPlatnosci.GetMethods()
                .FirstOrDefault(m => m.Name == name
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == fpEntityType)
                ?? throw new InvalidOperationException($"{name}(FormaPlatnosci) not found on IPlatnosciNaDokumencie");
        });

    // Map an OL taxRate string to the StawkiVat.Id (Guid). Behaviour identical to legacy.
    private Guid? LookupStawkaVatId(Microsoft.Data.SqlClient.SqlConnection conn, string? taxRate)
    {
        if (string.IsNullOrWhiteSpace(taxRate)) return null;
        var sym = taxRate.Trim().ToLowerInvariant() switch
        {
            "np" or "np." or "nieopodatkowane" => "nieop.",
            "zwolnione" or "zw." => "zw",
            var s => s
        };
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT TOP 1 Id FROM ModelDanychContainer.StawkiVat WHERE Symbol = @s";
            cmd.Parameters.AddWithValue("@s", sym);
            if (cmd.ExecuteScalar() is Guid g) return g;
        }
        catch (Exception ex) { _log.LogWarning("LookupStawkaVatId('{r}') failed: {e}", taxRate, ex.Message); }
        return null;
    }
}
