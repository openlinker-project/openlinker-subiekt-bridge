// BankAccountProbe - throwaway Phase 0 live probe for issue #1
// (bank account / payment method per invoice).
//
// Subcommands:
//   explore                     - dump payment/bank-account related surface of the
//                                 FV business object, its Dane entity and the
//                                 IFormyPlatnosci / IRachunkiBankowe facades.
//   baseline                    - create a throwaway FV via the default payment path
//                                 (DodajPlatnosciDomyslne) and read back payment rows.
//   transfer --fp N --account N - create FV with DodajPlatnoscOdroczona(FormaPlatnosci)
//                                 + explicit RachunekBankowyMojejFirmy snapshot.
//   cash --fp N                 - create FV with DodajPlatnoscNatychmiastowa(FormaPlatnosci).
//   default-flag --account N    - try flipping the seller default-account flag via a
//                                 Sfera BO save (stretch-goal gate), then restore.
//   podmioty                     - issue #3 live-topology probe: dump every seller
//                                 (MojaFirma) Podmiot (Typ=2/Podtyp=11) plus its bank
//                                 accounts, and look for an Oddzial/JednostkaOrganizacyjna
//                                 schema, to decide multi-Podmiot vs. Podmiot-with-Oddzialy
//                                 before designing the invoice-contract payer selector.
//   oddzial-test --oddzial N     - issue #5 write-side probe: create a THROWAWAY cash FV,
//     --stanowisko N              set Dane.Oddzial + Dane.StanowiskoKasowe explicitly, and
//                                 save it, to confirm whether Sfera rejects a Stanowisko
//                                 Kasowe linked to a DIFFERENT Oddzial than the document's
//                                 (StanowiskoKasoweZInnejJednostkiOrganizacyjnejBlad) or
//                                 accepts an unlinked one from any Oddzial. Leaves the saved
//                                 document in place (Sfera FV cannot be un-saved by the API;
//                                 acceptable on a demo DB per explicit user sign-off).
//
// Config: reads the "Sfera" section of the legacy POC appsettings
// (default C:\subiekt-poc\bridge\SferaApi\appsettings.json, override with --config).

using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Subiekt.Bridge.Infrastructure.Sfera;

var argsList = args.ToList();
string GetOpt(string name, string fallback)
{
    var i = argsList.IndexOf(name);
    return i >= 0 && i + 1 < argsList.Count ? argsList[i + 1] : fallback;
}

var command = argsList.FirstOrDefault(a => !a.StartsWith("--")) ?? "explore";

// Throwaway Phase-0 tool, not part of bridge.sln's default build. The bundled default
// only exists on the original dev machine — prefer --config, then the SFERA_BRIDGE_APPSETTINGS
// env var (same override pattern as tools/SferaInspect's SFERA_BINARIES), and warn loudly
// when silently falling back to the hardcoded path so a run on another machine fails fast
// and obviously instead of a confusing "file not found" deep inside JsonDocument.Parse.
const string DefaultConfigPath = @"C:\subiekt-poc\bridge\SferaApi\appsettings.json";
var configPathFromEnv = Environment.GetEnvironmentVariable("SFERA_BRIDGE_APPSETTINGS");
var configPath = GetOpt("--config", configPathFromEnv ?? DefaultConfigPath);

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(argsList.Contains("--debug") ? LogLevel.Debug : LogLevel.Information));
var log = loggerFactory.CreateLogger("Probe");

if (!argsList.Contains("--config") && configPathFromEnv is null)
{
    log.LogWarning(
        "No --config or SFERA_BRIDGE_APPSETTINGS set — defaulting to the original dev machine's " +
        "path ({path}), which will not exist elsewhere.", DefaultConfigPath);
}

// ---- load Sfera options from the POC appsettings ----
var json = JsonDocument.Parse(File.ReadAllText(configPath));
var s = json.RootElement.GetProperty("Sfera");
var deploymentRoot = GetOpt("--deployment", "");
var opt = new SferaOptions
{
    BinariesDir = deploymentRoot != "" ? deploymentRoot + @"\Binaries" : s.GetProperty("BinariesDir").GetString()!,
    ConfigDir = deploymentRoot != "" ? deploymentRoot + @"\Config" : s.GetProperty("ConfigDir").GetString()!,
    TempDir = deploymentRoot != "" ? deploymentRoot + @"\Work" : s.GetProperty("TempDir").GetString()!,
    DeploymentName = s.GetProperty("DeploymentName").GetString()!,
    SqlServer = s.GetProperty("SqlServer").GetString()!,
    SqlDatabase = GetOpt("--db", s.GetProperty("SqlDatabase").GetString()!),
    SqlUseWindowsAuth = s.GetProperty("SqlUseWindowsAuth").GetBoolean(),
    SqlEncrypt = false,
    NexoUser = s.GetProperty("NexoUser").GetString()!,
    NexoPassword = s.GetProperty("NexoPassword").GetString()!,
    AutoConnect = false,
};

var sqlConnString = new SqlConnectionStringBuilder
{
    DataSource = opt.SqlServer,
    InitialCatalog = opt.SqlDatabase,
    IntegratedSecurity = opt.SqlUseWindowsAuth,
    Encrypt = false,
    TrustServerCertificate = true,
}.ConnectionString;

SferaBoot.InstallAssemblyResolver(opt.BinariesDir);

var session = new SferaSession(Options.Create(opt), loggerFactory.CreateLogger<SferaSession>());
log.LogInformation("Connecting to Sfera ({db})...", opt.SqlDatabase);
session.Connect();
log.LogInformation("Connected.");

var acc = new SferaObjectAccessor(log);
const BindingFlags F = SferaObjectAccessor.Flags;

try
{
    switch (command)
    {
        case "explore": Explore(); break;
        case "baseline": CreateFv(payment: null); break;
        case "service": ServiceBaseline(); break;
        case "explore2": Explore2(); break;
        case "transfer":
            CreateFv(new PaymentPlan(
                Kind: "transfer",
                FormaPlatnosciId: int.Parse(GetOpt("--fp", "2")),
                BankAccountId: int.Parse(GetOpt("--account", "100007"))));
            break;
        case "cash":
            CreateFv(new PaymentPlan(
                Kind: "cash",
                FormaPlatnosciId: int.Parse(GetOpt("--fp", "1")),
                BankAccountId: null));
            break;
        case "default-flag": DefaultFlagProbe(int.Parse(GetOpt("--account", "100007"))); break;
        case "podmioty": PodmiotyProbe(); break;
        case "oddzial-test":
            OddzialStanowiskoTest(
                oddzialId: int.Parse(GetOpt("--oddzial", "100001")),
                stanowiskoId: int.Parse(GetOpt("--stanowisko", "100065")),
                setOddzial: !argsList.Contains("--skip-oddzial"),
                setStanowisko: !argsList.Contains("--skip-stanowisko"),
                magazynSymbol: GetOpt("--magazyn", "MAG"));
            break;
        default: log.LogError("Unknown command {c}", command); break;
    }
}
finally
{
    session.Dispose();
}

return 0;

// =====================================================================

void DumpMembers(string label, Type t, string[]? keywords = null)
{
    Console.WriteLine($"\n===== {label} ({t.FullName}) =====");
    foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance).OrderBy(x => x.MemberType).ThenBy(x => x.Name))
    {
        string line = m switch
        {
            MethodInfo mi when !mi.IsSpecialName =>
                $"  method {mi.ReturnType.Name} {mi.Name}({string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})",
            PropertyInfo pi => $"  prop {pi.PropertyType.Name} {pi.Name} {{ {(pi.CanRead ? "get; " : "")}{(pi.CanWrite ? "set; " : "")}}}",
            _ => "",
        };
        if (line == "") continue;
        if (keywords is null || keywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine(line);
    }
}

object GetFacade(string typeName)
{
    var t = SferaReflection.RequireType(typeName);
    return session.Uchwyt.PodajObiektTypu(t);
}

void Explore()
{
    var kw = new[] { "Platnosc", "Platnosci", "Rachun", "Forma", "Kwota", "Dan", "Jednostk", "Kontekst", "UnitOfWork", "Podaj", "Wszystkie", "Znajdz", "Utworz", "Zapisz", "Dane" };

    // 1. Facades
    foreach (var name in new[]
    {
        "InsERT.Moria.Kasa.IFormyPlatnosci, InsERT.Moria.API",
        "InsERT.Moria.Kasa.IFormyPlatnosciDane, InsERT.Moria.API",
        "InsERT.Moria.Bank.IRachunkiBankowe, InsERT.Moria.API",
        "InsERT.Moria.Bank.IRachunkiBankoweDane, InsERT.Moria.API",
    })
    {
        try
        {
            var facade = GetFacade(name);
            DumpMembers("FACADE " + name, facade.GetType());
            foreach (var iface in facade.GetType().GetInterfaces().Where(i => i.FullName!.Contains("Moria")))
                DumpMembers("  implements " + iface.Name, iface);
        }
        catch (Exception ex) { Console.WriteLine($"FACADE {name}: FAILED - {ex.Message}"); }
    }

    // 2. FV business object + Dane
    var iDok = SferaReflection.RequireType("InsERT.Moria.Dokumenty.Logistyka.IDokumentySprzedazy, InsERT.Moria.Dokumenty.Logistyka");
    var facadeDok = session.Uchwyt.PodajObiektTypu(iDok);
    var bo = iDok.GetMethod("UtworzFaktureSprzedazy", F)!.Invoke(facadeDok, null)!;
    DumpMembers("FV BO (payment/account keywords)", bo.GetType(), new[] { "Platnosc", "Rachun", "Forma" });

    var dane = bo.GetType().GetProperty("Dane", F)!.GetValue(bo)!;
    DumpMembers("FV Dane (payment/account keywords)", dane.GetType(), new[] { "Platnosc", "Rachun", "Forma" });

    // 3. What does DodajPlatnosciDomyslne need - is Dane.RachunkiBankowe preset?
    Console.WriteLine("\nDane.RachunkiBankowe (fresh BO) = " + (acc.GetProperty(dane, dane.GetType(), "RachunkiBankowe") ?? "NULL"));
    Console.WriteLine("Dane.FormaPlatnosci (fresh BO)  = " + (acc.GetProperty(dane, dane.GetType(), "FormaPlatnosci") ?? "NULL"));
}

(int kontrahentId, string symbol) ResolveFixtures(string magazynSymbol = "MAG")
{
    using var conn = new SqlConnection(sqlConnString);
    conn.Open();
    int kontrahentId;
    if (GetOpt("--kontrahent", "") is var k && k != "")
        kontrahentId = int.Parse(k);
    else
    {
        using var c1 = conn.CreateCommand();
        c1.CommandText = @"SELECT TOP 1 Id FROM ModelDanychContainer.Podmioty WHERE Typ = 2 AND Podtyp = 7 ORDER BY Id";
        kontrahentId = (int)c1.ExecuteScalar()!;
    }
    using var c2 = conn.CreateCommand();
    c2.CommandText = @"SELECT TOP 1 a.Symbol FROM ModelDanychContainer.Asortymenty a
                       JOIN ModelDanychContainer.StanyMagazynowe sm ON sm.Asortyment_Id = a.Id AND sm.IloscDostepna > 1
                       JOIN ModelDanychContainer.Magazyny m ON m.Id = sm.Magazyn_Id AND m.Symbol = @mag
                       WHERE a.IsInRecycleBin = 0 ORDER BY a.Id";
    c2.Parameters.AddWithValue("@mag", magazynSymbol);
    var symbol = (string?)c2.ExecuteScalar() ?? throw new InvalidOperationException($"no product with stock in magazyn '{magazynSymbol}'");
    return (kontrahentId, symbol);
}

object? ResolveEntityViaDaneContext(object dane, string navProperty, int id, string entityTypeName, string idProp = "Id")
{
    // Strategy: EF-attached entity lookup through the Dane entity's data context.
    // Try well-known context accessors reflectively.
    var daneType = dane.GetType();
    foreach (var ctxProp in new[] { "Kontekst", "JednostkaPracy", "UnitOfWork", "DataContext", "Context" })
    {
        var p = daneType.GetProperty(ctxProp, F);
        if (p == null) continue;
        Console.WriteLine($"  [ctx] Dane.{ctxProp} = {p.GetValue(dane)?.GetType().FullName ?? "NULL"}");
    }
    return null;
}

void Explore2()
{
    var iDok2 = SferaReflection.RequireType("InsERT.Moria.Dokumenty.Logistyka.IDokumentySprzedazy, InsERT.Moria.Dokumenty.Logistyka");
    var fac2 = GetFacade("InsERT.Moria.Dokumenty.Logistyka.IDokumentySprzedazy, InsERT.Moria.Dokumenty.Logistyka");
    var bo2 = iDok2.GetMethod("UtworzFaktureSprzedazy", F)!.Invoke(fac2, null)!;
    DumpMembers("FV BO (save/veto keywords)", bo2.GetType(),
        new[] { "Zapisz", "Mozna", "Komunikat", "Powod", "Blad", "Bledy", "Ostrzez", "Invalid", "Wynik", "Zatwierd" });
}

void ServiceBaseline()
{
    // Proven production path: SferaDokumentySprzedazyService.UtworzFaktura.
    var (kontrahentId, symbol) = ResolveFixtures();
    log.LogInformation("Fixtures: kontrahent={k}, symbol={s}", kontrahentId, symbol);
    var svc = new SferaDokumentySprzedazyService(loggerFactory.CreateLogger<SferaDokumentySprzedazyService>(), Options.Create(opt));
    var input = new SferaInvoiceInput(
        KontrahentId: kontrahentId,
        DataSprzedazy: DateTime.Now,
        DataWydania: DateTime.Now,
        Lines: new[] { new SferaInvoiceLineInput(symbol, 1m, 123m, "23", null) });
    var (id, numer) = svc.UtworzFaktura(session, input);
    log.LogInformation("Service created FV id={id} numer={n}", id, numer);
    ReadBack(id);
}

void CreateFv(PaymentPlan? payment)
{
    var (kontrahentId, symbol) = ResolveFixtures();
    log.LogInformation("Fixtures: kontrahent={k}, symbol={s}", kontrahentId, symbol);

    var iDok = SferaReflection.RequireType("InsERT.Moria.Dokumenty.Logistyka.IDokumentySprzedazy, InsERT.Moria.Dokumenty.Logistyka");
    var facade = session.Uchwyt.PodajObiektTypu(iDok);
    var bo = iDok.GetMethod("UtworzFaktureSprzedazy", F)!.Invoke(facade, null)!;
    var boType = bo.GetType();
    var dane = boType.GetProperty("Dane", F)!.GetValue(bo)!;
    var daneType = dane.GetType();

    acc.SetBoolFlag(bo, boType, "IgnorujBlokade", true);
    acc.SetBoolFlag(bo, boType, "WylaczBlokowanieStanowPrzezRezerwacjeIlosciowa", true);
    acc.SetBoolFlag(bo, boType, "NieSprawdzajRealizacji", true);

    boType.GetMethod("UstawNabywceWedlugId", F, new[] { typeof(int) })!.Invoke(bo, new object[] { kontrahentId });
    acc.SetProperty(dane, daneType, "DataSprzedazy", DateTime.Now);
    acc.SetProperty(dane, daneType, "DataWydaniaWystawienia", DateTime.Now);

    var poz = boType.GetMethod("Dodaj", F, new[] { typeof(string) })!.Invoke(bo, new object[] { symbol })!;
    acc.SetProperty(poz, poz.GetType(), "Ilosc", 1m);
    var cena = poz.GetType().GetProperty("Cena", F)?.GetValue(poz);
    if (cena is not null)
    {
        acc.SetProperty(cena, cena.GetType(), "BruttoPrzedRabatem", 123m);
        acc.SetProperty(cena, cena.GetType(), "BruttoPoRabacie", 123m);
        acc.SetProperty(poz, poz.GetType(), "CenaRecznieEdytowana", true);
    }

    acc.InvokeIfExists(bo, boType, "Przelicz");

    if (payment is null)
    {
        log.LogInformation("BASELINE: DodajPlatnosciDomyslne + Natychmiastowa (deep diagnostics)");
        try { boType.GetMethod("DodajPlatnosciDomyslne", F, Type.EmptyTypes)?.Invoke(bo, null); log.LogInformation("DodajPlatnosciDomyslne OK"); }
        catch (TargetInvocationException tie) { log.LogError("DodajPlatnosciDomyslne threw: {t}: {m}", tie.InnerException?.GetType().Name, tie.InnerException?.Message); }
        try { boType.GetMethod("DodajDomyslnaPlatnoscNatychmiastowaNaKwoteDokumentu", F, Type.EmptyTypes)?.Invoke(bo, null); log.LogInformation("DodajDomyslnaPlatnoscNatychmiastowa OK"); }
        catch (TargetInvocationException tie)
        {
            log.LogError("DodajDomyslnaPlatnoscNatychmiastowa threw: {t}: {m}\n{st}",
                tie.InnerException?.GetType().Name, tie.InnerException?.Message,
                tie.InnerException?.StackTrace?.Split('\n').FirstOrDefault());
        }

        DumpPayments(dane, daneType);
        RemoveInvalidZeroPayments(bo, boType, dane, daneType);
        DumpPayments(dane, daneType);
    }
    else
    {
        ApplyPayment(bo, boType, dane, daneType, payment);
    }

    acc.InvokeIfExists(bo, boType, "IgnorujBlokadeRealizacjiPozycji");
    acc.InvokeIfExists(bo, boType, "AutoSymbol");
    acc.InvokeIfExists(bo, boType, "NadajNumer");

    var waliduj = boType.GetMethod("Waliduj", F, Type.EmptyTypes);
    try { waliduj?.Invoke(bo, null); log.LogInformation("Waliduj OK"); }
    catch (TargetInvocationException tie) { log.LogError("Waliduj FAILED: {m}", tie.InnerException?.Message); }

    var saved = boType.GetMethod("Zapisz", F, Type.EmptyTypes)!.Invoke(bo, null);
    log.LogInformation("Zapisz() = {r}", saved);
    if (saved is not true)
    {
        log.LogError("Save failed: {e}", acc.CollectValidationErrors(bo, boType, includeDocumentLevel: true, includeStateHint: true));
        try
        {
            if (boType.GetProperty("InvalidData", F)?.GetValue(bo) is IEnumerable inv)
                foreach (var ent in inv)
                {
                    if (ent is null) continue;
                    Console.WriteLine("  [InvalidData] " + Describe(ent));
                    if (ent is System.ComponentModel.IDataErrorInfo dei)
                        foreach (var p in ent.GetType().GetProperties(F))
                        {
                            if (p.GetIndexParameters().Length > 0) continue;
                            string? err = null;
                            try { err = dei[p.Name]; } catch { }
                            if (!string.IsNullOrEmpty(err)) Console.WriteLine($"    [IDataErrorInfo] {p.Name}: {err}");
                        }
                }
        }
        catch (Exception ex) { log.LogWarning("InvalidData dump failed: {m}", ex.Message); }
        return;
    }

    var id = (int)acc.GetProperty(dane, daneType, "Id")!;
    log.LogInformation("Saved FV id={id}", id);
    ReadBack(id);
}

void ApplyPayment(object bo, Type boType, object dane, Type daneType, PaymentPlan plan)
{
    log.LogInformation("PAYMENT PLAN: {kind} fp={fp} account={acc}", plan.Kind, plan.FormaPlatnosciId, plan.BankAccountId);

    // Resolve the FormaPlatnosci ENTITY inside the DOCUMENT's own unit of work:
    // set the FK and let EF fixup materialize the nav (fresh BO context has lazy
    // loading). Fallback: facade Znajdz(expression) -> BO -> .Dane entity.
    var fp = ResolveFormaPlatnosci(dane, plan.FormaPlatnosciId)
        ?? throw new InvalidOperationException($"could not resolve FormaPlatnosci {plan.FormaPlatnosciId}");
    log.LogInformation("Resolved FormaPlatnosci: '{n}' (Id={id})",
        acc.GetProperty(fp, fp.GetType(), "Nazwa"), acc.GetProperty(fp, fp.GetType(), "Id"));

    // Set the document's payment form.
    try { daneType.GetProperty("FormaPlatnosci", F)?.SetValue(dane, fp); log.LogInformation("Dane.FormaPlatnosci set"); }
    catch (Exception ex) { log.LogWarning("Dane.FormaPlatnosci set failed: {m}", ex.Message); }

    // Explicit payment via the IPlatnosciNaDokumencie interface (methods are
    // explicitly implemented on the BO, so lookup must go through the interface type).
    Type iPlatnosci;
    try { iPlatnosci = SferaReflection.RequireType("InsERT.Moria.Dokumenty.Logistyka.IPlatnosciNaDokumencie, InsERT.Moria.API"); }
    catch { iPlatnosci = SferaReflection.RequireType("InsERT.Moria.Dokumenty.Logistyka.IPlatnosciNaDokumencie, InsERT.Moria.Dokumenty.Logistyka"); }

    var fpEntityType2 = SferaReflection.RequireType("InsERT.Moria.ModelDanych.FormaPlatnosci, InsERT.Moria.ModelDanych");
    var addName = plan.Kind == "cash" ? "DodajPlatnoscNatychmiastowa" : "DodajPlatnoscOdroczona";
    var add = iPlatnosci.GetMethods()
        .FirstOrDefault(m => m.Name == addName
            && m.GetParameters().Length == 1
            && m.GetParameters()[0].ParameterType == fpEntityType2)
        ?? throw new InvalidOperationException($"{addName}(FormaPlatnosci) not found on {iPlatnosci.Name}");
    try
    {
        var result = add.Invoke(bo, new[] { fp });
        log.LogInformation("{m}(fp) via interface -> {t}", addName, result?.GetType().Name ?? "null");
    }
    catch (TargetInvocationException tie) { log.LogError("{m} threw: {e}", addName, tie.InnerException?.Message); }

    // Re-assert the document's payment form after the payment add.
    try { daneType.GetProperty("FormaPlatnosci", F)?.SetValue(dane, fp); } catch { }

    if (plan.Kind == "transfer" && plan.BankAccountId is int accountId)
    {
        string accName, accNumber;
        using (var conn = new SqlConnection(sqlConnString))
        {
            conn.Open();
            using var c = conn.CreateCommand();
            c.CommandText = @"SELECT cgf.Nazwa, rb.Numer FROM ModelDanychContainer.CentraGromadzeniaFinansow_RachunekBankowy rb
                              JOIN ModelDanychContainer.CentraGromadzeniaFinansow cgf ON cgf.Id = rb.Id WHERE rb.Id = @id";
            c.Parameters.AddWithValue("@id", accountId);
            using var r = c.ExecuteReader();
            if (!r.Read()) throw new InvalidOperationException($"bank account {accountId} not found");
            accName = r.GetString(0);
            accNumber = r.GetString(1);
        }
        log.LogInformation("Selected seller account: '{n}' {num}", accName, accNumber);

        // Write the seller-account SNAPSHOT on the document (RachunekBankowyDokumentu
        // is 1:1 with the document and pre-attached on a fresh BO).
        var rbd = daneType.GetProperty("RachunkiBankowe", F)?.GetValue(dane);
        if (rbd is null) { log.LogWarning("Dane.RachunkiBankowe is NULL"); }
        else
        {
            var mfProp = rbd.GetType().GetProperty("RachunekBankowyMojejFirmy", F)!;
            var snapshot = mfProp.GetValue(rbd);
            if (snapshot is null)
            {
                var snapType = SferaReflection.RequireType("InsERT.Moria.ModelDanych.DaneRachunkuBankowego, InsERT.Moria.ModelDanych");
                snapshot = Activator.CreateInstance(snapType)!;
                if (mfProp.CanWrite) mfProp.SetValue(rbd, snapshot);
            }
            acc.SetProperty(snapshot, snapshot.GetType(), "Nazwa", accName);
            acc.SetProperty(snapshot, snapshot.GetType(), "Numer", accNumber);
            log.LogInformation("Seller-account snapshot set: '{n}' {num}", accName, accNumber);
        }
    }

    DumpPayments(dane, daneType);
    RemoveInvalidZeroPayments(bo, boType, dane, daneType);
    DumpPayments(dane, daneType);
}

void DumpPayments(object dane, Type daneType)
{
    try
    {
        if (acc.GetProperty(dane, daneType, "PlatnosciDokumentow") is IEnumerable rows)
            foreach (var row in rows)
            {
                Console.WriteLine("  [payment] " + Describe(row));
                if (row is System.ComponentModel.IDataErrorInfo dei)
                    foreach (var p in row.GetType().GetProperties(F))
                    {
                        if (p.GetIndexParameters().Length > 0) continue;
                        string? err = null;
                        try { err = dei[p.Name]; } catch { }
                        if (!string.IsNullOrEmpty(err)) Console.WriteLine($"    [IDataErrorInfo] {p.Name}: {err}");
                    }
            }
    }
    catch (Exception ex) { log.LogWarning("payment dump failed: {m}", ex.Message); }
}

// The demo environment's DodajPlatnosciDomyslne emits a stray zero-amount payment
// row that fails entity validation and vetoes Zapisz(). Remove zero rows via the
// BO's own payments API (bo.Platnosci.Usun).
void RemoveInvalidZeroPayments(object bo, Type boType, object dane, Type daneType)
{
    try
    {
        var platnosci = boType.GetProperty("Platnosci", F)?.GetValue(bo);
        if (platnosci is null) { log.LogWarning("bo.Platnosci is null"); return; }
        var platnoscType = SferaReflection.RequireType("InsERT.Moria.ModelDanych.PlatnoscDokumentu, InsERT.Moria.ModelDanych");
        var usun = platnosci.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Usun"
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == platnoscType);
        if (usun is null) { log.LogWarning("Platnosci.Usun(PlatnoscDokumentu) not found"); return; }

        if (acc.GetProperty(dane, daneType, "PlatnosciDokumentow") is not IEnumerable rows) return;
        var toRemove = new List<object>();
        foreach (var row in rows)
        {
            var kwota = acc.GetProperty(row, row.GetType(), "KwotaPlatnosci");
            if (kwota is decimal d && d == 0m) toRemove.Add(row);
        }
        foreach (var row in toRemove)
        {
            usun.Invoke(platnosci, new[] { row });
            log.LogInformation("Removed zero-amount payment row");
        }
    }
    catch (Exception ex) { log.LogWarning("RemoveInvalidZeroPayments failed: {m}", ex.InnerException?.Message ?? ex.Message); }
}

string Describe(object entity)
{
    var t = entity.GetType();
    var parts = new List<string>();
    foreach (var name in new[] { "Id", "Kwota", "KwotaPlatnosci", "KwotaDokumentu", "TerminPlatnosci", "Termin", "Rodzaj", "Typ" })
    {
        var p = t.GetProperty(name, F);
        if (p?.GetIndexParameters().Length == 0)
        {
            object? v = null;
            try { v = p.GetValue(entity); } catch { }
            if (v is not null) parts.Add($"{name}={v}");
        }
    }
    var fpProp = t.GetProperty("FormaPlatnosci", F);
    if (fpProp is not null)
    {
        try
        {
            var fp = fpProp.GetValue(entity);
            if (fp is not null) parts.Add($"FormaPlatnosci={acc.GetProperty(fp, fp.GetType(), "Nazwa")}");
        }
        catch { }
    }
    return $"{t.Name}: {string.Join(" ", parts)}";
}

object? ResolveFormaPlatnosci(object dane, int fpId)
{
    var daneType = dane.GetType();

    // Strategy 1: set the FK on the document entity and let EF fixup/lazy-load
    // materialize the nav INSIDE the document's own unit of work.
    try
    {
        var idProp = daneType.GetProperty("FormaPlatnosciId", F);
        if (idProp is not null)
        {
            idProp.SetValue(dane, fpId);
            var nav = daneType.GetProperty("FormaPlatnosci", F)?.GetValue(dane);
            if (nav is not null && Equals(acc.GetProperty(nav, nav.GetType(), "Id"), fpId))
            {
                log.LogInformation("Strategy 1 (FK fixup) resolved FormaPlatnosci {id}", fpId);
                return nav;
            }
            log.LogInformation("Strategy 1 (FK fixup): nav = {v}", nav is null ? "NULL" : "wrong id");
        }
    }
    catch (Exception ex) { log.LogWarning("Strategy 1 failed: {m}", ex.Message); }

    // Strategy 2: facade Znajdz(Expression<Func<FormaPlatnosci,bool>>) -> IFormaPlatnosci BO -> .Dane entity.
    try
    {
        var fpType = SferaReflection.RequireType("InsERT.Moria.ModelDanych.FormaPlatnosci, InsERT.Moria.ModelDanych");
        var facade = GetFacade("InsERT.Moria.Kasa.IFormyPlatnosci, InsERT.Moria.API");
        var param = System.Linq.Expressions.Expression.Parameter(fpType, "x");
        var body = System.Linq.Expressions.Expression.Equal(
            System.Linq.Expressions.Expression.Property(param, "Id"),
            System.Linq.Expressions.Expression.Constant(fpId));
        var funcType = typeof(Func<,>).MakeGenericType(fpType, typeof(bool));
        var lambda = System.Linq.Expressions.Expression.Lambda(funcType, body, param);
        var exprType = typeof(System.Linq.Expressions.Expression<>).MakeGenericType(funcType);

        var znajdz = facade.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Znajdz"
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == exprType);
        if (znajdz is null) { log.LogWarning("Strategy 2: no Znajdz(Expression) overload"); return null; }

        var fpBo = znajdz.Invoke(facade, new object[] { lambda });
        if (fpBo is null) { log.LogWarning("Strategy 2: Znajdz returned null"); return null; }
        var entity = fpBo.GetType().GetProperty("Dane", F)?.GetValue(fpBo);
        log.LogInformation("Strategy 2 (facade Znajdz) resolved: {t}", entity?.GetType().Name ?? "NULL");
        return entity;
    }
    catch (Exception ex)
    {
        log.LogWarning("Strategy 2 failed: {m}", ex.InnerException?.Message ?? ex.Message);
        return null;
    }
}

void ReadBack(int docId)
{
    using var conn = new SqlConnection(sqlConnString);
    conn.Open();

    Console.WriteLine($"\n===== READ-BACK doc {docId} =====");

    void Dump(string title, string sql)
    {
        Console.WriteLine($"\n--- {title} ---");
        try
        {
            using var c = conn.CreateCommand();
            c.CommandText = sql;
            c.Parameters.AddWithValue("@id", docId);
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var parts = new List<string>();
                for (var i = 0; i < r.FieldCount; i++)
                    parts.Add($"{r.GetName(i)}={(r.IsDBNull(i) ? "NULL" : r.GetValue(i))}");
                Console.WriteLine("  " + string.Join(" | ", parts));
            }
        }
        catch (Exception ex) { Console.WriteLine("  ERROR: " + ex.Message); }
    }

    // Discover payment-related columns on Dokumenty, then select them for the doc.
    var cols = new List<string> { "NumerWewnetrzny_PelnaSygnatura" };
    using (var cc = conn.CreateCommand())
    {
        cc.CommandText = @"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
                           WHERE TABLE_SCHEMA='ModelDanychContainer' AND TABLE_NAME='Dokumenty'
                             AND (COLUMN_NAME LIKE '%FormaPlatnosci%' OR COLUMN_NAME LIKE '%Rachun%')";
        using var r = cc.ExecuteReader();
        while (r.Read()) cols.Add(r.GetString(0));
    }
    Dump("Dokumenty payment columns",
        $"SELECT {string.Join(", ", cols.Select(c => "[" + c + "]"))} FROM ModelDanychContainer.Dokumenty WHERE Id = @id");

    Dump("FormyPlatnosciDokumentu",
        @"SELECT * FROM ModelDanychContainer.FormyPlatnosciDokumentu WHERE Dokument_Id = @id");
    Dump("PlatnosciDokumentow",
        @"SELECT DokumentId, LP, KwotaDokumentu, KwotaPlatnosci, RodzajPlatnosci, Termin, TerminDni, FormaPlatnosci_Id
          FROM ModelDanychContainer.PlatnosciDokumentow WHERE DokumentId = @id");
    Dump("RachunkiBankoweDokumentow (1:1 by doc id)",
        @"SELECT Id, RachunekBankowyMojejFirmy_Nazwa, RachunekBankowyMojejFirmy_Numer
          FROM ModelDanychContainer.RachunkiBankoweDokumentow WHERE Id = @id");
}

void DefaultFlagProbe(int accountId)
{
    // Read current state
    using (var conn = new SqlConnection(sqlConnString))
    {
        conn.Open();
        using var c = conn.CreateCommand();
        c.CommandText = @"SELECT Id, PodstawowyDlaWaluty, WlascicielPodstawowego_Id, Wlasciciel_Id
                          FROM ModelDanychContainer.CentraGromadzeniaFinansow_RachunekBankowy WHERE Id = @id";
        c.Parameters.AddWithValue("@id", accountId);
        using var r = c.ExecuteReader();
        while (r.Read())
            Console.WriteLine($"BEFORE: Id={r.GetInt32(0)} PodstawowyDlaWaluty={r.GetBoolean(1)} WlascicielPodstawowego={(r.IsDBNull(2) ? "NULL" : r.GetInt32(2))} Wlasciciel={(r.IsDBNull(3) ? "NULL" : r.GetInt32(3))}");
    }

    // The "Podstawowy" flag is owned by the PODMIOT side (Podmiot.RachunekPodstawowy);
    // the RachunekBankowy BO does not sponsor WlascicielPodstawowego (verified:
    // UnsponsoredModificationException). Load the OWNER's Podmiot business object.
    int ownerId;
    using (var conn = new SqlConnection(sqlConnString))
    {
        conn.Open();
        using var c = conn.CreateCommand();
        c.CommandText = @"SELECT Wlasciciel_Id FROM ModelDanychContainer.CentraGromadzeniaFinansow_RachunekBankowy WHERE Id = @id";
        c.Parameters.AddWithValue("@id", accountId);
        ownerId = (int)c.ExecuteScalar()!;
    }

    var podmiotType = SferaReflection.RequireType("InsERT.Moria.ModelDanych.Podmiot, InsERT.Moria.ModelDanych");
    var podmiotBo = FindByIdViaFacade("InsERT.Moria.Klienci.IPodmioty, InsERT.Moria.Klienci", podmiotType, ownerId)
        ?? throw new InvalidOperationException($"podmiot {ownerId} not found via facade");
    var podmiotEntity = podmiotBo.GetType().GetProperty("Dane", F)!.GetValue(podmiotBo)!;
    var pet = podmiotEntity.GetType();

    var beforeRb = acc.GetProperty(podmiotEntity, pet, "RachunekPodstawowy");
    Console.WriteLine($"Podmiot {ownerId} RachunekPodstawowy(before) = {(beforeRb is null ? "NULL" : acc.GetProperty(beforeRb, beforeRb.GetType(), "Id"))}");

    // Find the target account entity in the podmiot's OWN context (its RachunkiBankowe collection).
    object? targetInCtx = null;
    if (acc.GetProperty(podmiotEntity, pet, "Rachunki") is IEnumerable rbs)
        foreach (var rb in rbs)
        {
            Console.WriteLine($"  [podmiot.Rachunki] Id={acc.GetProperty(rb, rb.GetType(), "Id")} Numer={acc.GetProperty(rb, rb.GetType(), "Numer")}");
            if (Equals(acc.GetProperty(rb, rb.GetType(), "Id"), accountId)) { targetInCtx = rb; }
        }
    if (targetInCtx is null)
    {
        foreach (var p in pet.GetProperties(F).Where(p => p.Name.Contains("Rachun", StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"  [Podmiot prop] {p.PropertyType.Name} {p.Name}");
        throw new InvalidOperationException($"account {accountId} not in podmiot.RachunkiBankowe");
    }

    acc.SetProperty(podmiotEntity, pet, "RachunekPodstawowy", targetInCtx);
    var saveOk = podmiotBo.GetType().GetMethod("Zapisz", F, Type.EmptyTypes)?.Invoke(podmiotBo, null);
    Console.WriteLine($"Podmiot.Zapisz() after set = {saveOk}");

    var beforeRbId = beforeRb is null ? (int?)null : (int)acc.GetProperty(beforeRb, beforeRb.GetType(), "Id")!;

    using (var conn = new SqlConnection(sqlConnString))
    {
        conn.Open();
        using var c = conn.CreateCommand();
        c.CommandText = @"SELECT Id, WlascicielPodstawowego_Id, PodstawowyDlaWaluty
                          FROM ModelDanychContainer.CentraGromadzeniaFinansow_RachunekBankowy WHERE Wlasciciel_Id = (
                            SELECT Wlasciciel_Id FROM ModelDanychContainer.CentraGromadzeniaFinansow_RachunekBankowy WHERE Id = @id)";
        c.Parameters.AddWithValue("@id", accountId);
        using var r = c.ExecuteReader();
        while (r.Read())
            Console.WriteLine($"AFTER: Id={r.GetInt32(0)} WlascicielPodstawowego={(r.IsDBNull(1) ? "NULL" : r.GetInt32(1))} PodstawowyDlaWaluty={r.GetBoolean(2)}");
    }

    // Restore the previous primary account (leaves the DB as we found it).
    if (saveOk is true)
    {
        var restoreBo = FindByIdViaFacade("InsERT.Moria.Klienci.IPodmioty, InsERT.Moria.Klienci", podmiotType, ownerId)!;
        var restoreEntity = restoreBo.GetType().GetProperty("Dane", F)!.GetValue(restoreBo)!;
        object? restoreTarget = null;
        if (beforeRbId is int prevId && acc.GetProperty(restoreEntity, restoreEntity.GetType(), "Rachunki") is IEnumerable rbs2)
            foreach (var rb in rbs2)
                if (Equals(acc.GetProperty(rb, rb.GetType(), "Id"), prevId)) { restoreTarget = rb; break; }
        acc.SetProperty(restoreEntity, restoreEntity.GetType(), "RachunekPodstawowy", restoreTarget);
        var restoreOk = restoreBo.GetType().GetMethod("Zapisz", F, Type.EmptyTypes)?.Invoke(restoreBo, null);
        Console.WriteLine($"Restore Zapisz() = {restoreOk} (RachunekPodstawowy -> {(beforeRbId?.ToString() ?? "NULL")})");
    }
}

object? FindByIdViaFacade(string facadeTypeName, Type entityType, int id)
{
    var facade = GetFacade(facadeTypeName);
    var param = System.Linq.Expressions.Expression.Parameter(entityType, "x");
    var body = System.Linq.Expressions.Expression.Equal(
        System.Linq.Expressions.Expression.Property(param, "Id"),
        System.Linq.Expressions.Expression.Constant(id));
    var funcType = typeof(Func<,>).MakeGenericType(entityType, typeof(bool));
    var lambda = System.Linq.Expressions.Expression.Lambda(funcType, body, param);
    var exprType = typeof(System.Linq.Expressions.Expression<>).MakeGenericType(funcType);
    var znajdz = facade.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .FirstOrDefault(m => m.Name == "Znajdz"
            && m.GetParameters().Length == 1
            && m.GetParameters()[0].ParameterType == exprType);
    if (znajdz is null) { log.LogWarning("no Znajdz(Expression<Func<{t},bool>>) on facade", entityType.Name); return null; }
    return znajdz.Invoke(facade, new object[] { lambda });
}

// Issue #3 live-topology probe: enumerate every seller (MojaFirma) Podmiot and its
// bank accounts, then look for any Oddzial/JednostkaOrganizacyjna schema, so a future
// session can decide "multiple genuinely-separate Podmioty" vs. "one Podmiot with
// multiple Oddzialy" before designing the invoice-contract payer selector.
void PodmiotyProbe()
{
    // Numeric SQL Server column widths (tinyint/smallint/int) vary across the schema in
    // ways not worth hand-verifying per column (Typ/Podtyp turned out to be `tinyint` -
    // System.Byte - not `int`, caught on the third live run 2026-07-02). Convert.ToInt32
    // widens any integral SqlDbType without per-column guessing.
    static int AnyInt(SqlDataReader r, int i) => Convert.ToInt32(r.GetValue(i));

    using var conn = new SqlConnection(sqlConnString);
    conn.Open();

    // NOTE (issue #3/#5 Phase 1, verified live 2026-07-02 against Nexo_Demo_1): `Podmioty`
    // has NO `Nazwa` column (128-column schema dump confirmed) - the display-name column
    // is `NazwaSkrocona`. The original `owner.Nazwa` in this probe (and in PR #4's
    // SqlBankAccountsReader) threw `Invalid column name 'Nazwa'` on first live run.
    Console.WriteLine("\n===== Seller Podmioty (Typ=2 AND Podtyp=11) =====");
    using (var c = conn.CreateCommand())
    {
        c.CommandText = @"SELECT Id, NazwaSkrocona, Typ, Podtyp FROM ModelDanychContainer.Podmioty
                          WHERE Typ = 2 AND Podtyp = 11 ORDER BY Id";
        using var r = c.ExecuteReader();
        var count = 0;
        while (r.Read())
        {
            count++;
            // Typ/Podtyp are `tinyint` on the live schema, not `int` - AnyInt widens it.
            Console.WriteLine($"  Podmiot Id={AnyInt(r, 0)} NazwaSkrocona={(r.IsDBNull(1) ? "NULL" : r.GetString(1))} Typ={AnyInt(r, 2)} Podtyp={AnyInt(r, 3)}");
        }
        Console.WriteLine(count == 1
            ? "  -> exactly ONE seller Podmiot: today's TOP 1 assumption happened to hold on THIS database."
            : $"  -> {count} seller Podmioty found: TOP 1 was silently dropping {count - 1} payer(s)' accounts.");
    }

    Console.WriteLine("\n===== Bank accounts per seller Podmiot =====");
    using (var c = conn.CreateCommand())
    {
        c.CommandText = @"
            SELECT rb.Wlasciciel_Id, owner.NazwaSkrocona, rb.Id, cgf.Nazwa, rb.Numer, rb.Aktywny
            FROM ModelDanychContainer.CentraGromadzeniaFinansow_RachunekBankowy rb
            JOIN ModelDanychContainer.CentraGromadzeniaFinansow cgf ON cgf.Id = rb.Id
            LEFT JOIN ModelDanychContainer.Podmioty owner ON owner.Id = rb.Wlasciciel_Id
            WHERE rb.Wlasciciel_Id IN (SELECT Id FROM ModelDanychContainer.Podmioty WHERE Typ = 2 AND Podtyp = 11)
            ORDER BY rb.Wlasciciel_Id, rb.Id";
        using var r = c.ExecuteReader();
        while (r.Read())
            Console.WriteLine($"  Owner={AnyInt(r, 0)} ({(r.IsDBNull(1) ? "NULL" : r.GetString(1))})  Account={AnyInt(r, 2)} {(r.IsDBNull(3) ? "" : r.GetString(3))} Numer={(r.IsDBNull(4) ? "" : r.GetString(4))} Aktywny={r.GetBoolean(5)}");
    }

    Console.WriteLine("\n===== Oddzial / JednostkaOrganizacyjna schema check =====");
    using (var c = conn.CreateCommand())
    {
        c.CommandText = @"
            SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_NAME LIKE '%Oddzial%' OR TABLE_NAME LIKE '%JednostkaOrganizacyjna%' OR TABLE_NAME LIKE '%StanowiskoKasowe%'
            ORDER BY TABLE_NAME";
        using var r = c.ExecuteReader();
        var any = false;
        while (r.Read())
        {
            any = true;
            Console.WriteLine($"  table {r.GetString(0)}.{r.GetString(1)}");
        }
        if (!any)
            Console.WriteLine("  -> no Oddzial/JednostkaOrganizacyjna-named table found (schema may model branches differently, e.g. via Podmiot.Rodzic/Nadrzedny - check IPodmioty facade members reflectively if this comes back empty).");
    }

    Console.WriteLine("\n===== Podmiot columns referencing a parent/branch relationship =====");
    using (var c = conn.CreateCommand())
    {
        c.CommandText = @"
            SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'ModelDanychContainer' AND TABLE_NAME = 'Podmioty'
              AND (COLUMN_NAME LIKE '%Oddzial%' OR COLUMN_NAME LIKE '%Nadrzedn%' OR COLUMN_NAME LIKE '%Rodzic%' OR COLUMN_NAME LIKE '%Platnik%')
            ORDER BY COLUMN_NAME";
        using var r = c.ExecuteReader();
        var any = false;
        while (r.Read()) { any = true; Console.WriteLine($"  Podmioty.{r.GetString(0)}"); }
        if (!any)
            Console.WriteLine("  -> none found on Podmioty directly.");
    }

    // Added issue #5 Phase 1 (verified live 2026-07-02 against Nexo_Demo_1): Oddzial is
    // modeled as ModelDanychContainer.JednostkiOrganizacyjne_Oddzial (Id, Nazwa,
    // Centrala_Id, PodstawowyRachunekBankowy_Id) - independent of Podmiot count (this
    // demo DB has exactly 1 seller Podmiot but 2 Oddzialy, both pointing at the same
    // Centrala_Id). An Oddzial can carry its OWN default bank account
    // (PodstawowyRachunekBankowy_Id), separate from the Podmiot-level default.
    Console.WriteLine("\n===== Oddzialy (JednostkiOrganizacyjne_Oddzial) =====");
    using (var c = conn.CreateCommand())
    {
        c.CommandText = @"
            SELECT Id, Nazwa, Centrala_Id, PodstawowyRachunekBankowy_Id
            FROM ModelDanychContainer.JednostkiOrganizacyjne_Oddzial ORDER BY Id";
        using var r = c.ExecuteReader();
        var count = 0;
        while (r.Read())
        {
            count++;
            Console.WriteLine($"  Oddzial Id={AnyInt(r, 0)} Nazwa={(r.IsDBNull(1) ? "NULL" : r.GetString(1))} Centrala_Id={(r.IsDBNull(2) ? "NULL" : AnyInt(r, 2).ToString())} PodstawowyRachunekBankowy_Id={(r.IsDBNull(3) ? "NULL" : AnyInt(r, 3).ToString())}");
        }
        Console.WriteLine($"  -> {count} Oddzial(y) found.");
    }

    // Added issue #5 Phase 1: StanowiskoKasowe is a CentraGromadzeniaFinansow TPT subtype
    // (same pattern as RachunekBankowy - display name lives on the base `cgf.Nazwa` row,
    // not on the subtype table itself, which has no Nazwa either). The link table
    // StanowiskoKasoweJednostkaOrganizacyjna confirms each Stanowisko Kasowe belongs to
    // exactly one Oddzial (matches the `StanowiskoKasoweZInnejJednostkiOrganizacyjnejBlad`
    // reflection-dump evidence from issue #5's body).
    Console.WriteLine("\n===== Stanowiska Kasowe (CentraGromadzeniaFinansow_StanowiskoKasowe) =====");
    using (var c = conn.CreateCommand())
    {
        c.CommandText = @"
            SELECT sk.Id, cgf.Nazwa, sk.Symbol, sk.Opis, link.JednostkiOrganizacyjne_Id
            FROM ModelDanychContainer.CentraGromadzeniaFinansow_StanowiskoKasowe sk
            JOIN ModelDanychContainer.CentraGromadzeniaFinansow cgf ON cgf.Id = sk.Id
            LEFT JOIN ModelDanychContainer.StanowiskoKasoweJednostkaOrganizacyjna link ON link.StanowiskaKasowe_Id = sk.Id
            ORDER BY sk.Id";
        using var r = c.ExecuteReader();
        var count = 0;
        while (r.Read())
        {
            count++;
            Console.WriteLine($"  StanowiskoKasowe Id={AnyInt(r, 0)} Nazwa={(r.IsDBNull(1) ? "NULL" : r.GetString(1))} Symbol={(r.IsDBNull(2) ? "NULL" : r.GetString(2))} Opis={(r.IsDBNull(3) ? "" : r.GetString(3))} Oddzial_Id={(r.IsDBNull(4) ? "NULL (unlinked)" : AnyInt(r, 4).ToString())}");
        }
        Console.WriteLine($"  -> {count} Stanowisko(a) Kasowe found.");
    }
}

// Issue #5 write-side probe: create a throwaway Cash FV, stamp an explicit
// Oddzial + Stanowisko Kasowe on it, and observe whether Zapisz() accepts or
// rejects the combination - settles whether the StanowiskoKasoweJednostkaOrganizacyjna
// link table is a mandatory 1:1 FK or an optional restriction (see
// docs/spikes/podmioty-oddzial-stanowisko-probe-findings.md s.3 for the read-side
// half of this question).
void OddzialStanowiskoTest(int oddzialId, int stanowiskoId, bool setOddzial = true, bool setStanowisko = true, string magazynSymbol = "MAG")
{
    var (kontrahentId, symbol) = ResolveFixtures(magazynSymbol);
    log.LogInformation("Fixtures: kontrahent={k}, symbol={s}, oddzial={o}, stanowisko={st}", kontrahentId, symbol, oddzialId, stanowiskoId);

    var iDok = SferaReflection.RequireType("InsERT.Moria.Dokumenty.Logistyka.IDokumentySprzedazy, InsERT.Moria.Dokumenty.Logistyka");
    var facade = session.Uchwyt.PodajObiektTypu(iDok);
    var bo = iDok.GetMethod("UtworzFaktureSprzedazy", F)!.Invoke(facade, null)!;
    var boType = bo.GetType();
    var dane = boType.GetProperty("Dane", F)!.GetValue(bo)!;
    var daneType = dane.GetType();

    acc.SetBoolFlag(bo, boType, "IgnorujBlokade", true);
    acc.SetBoolFlag(bo, boType, "WylaczBlokowanieStanowPrzezRezerwacjeIlosciowa", true);
    acc.SetBoolFlag(bo, boType, "NieSprawdzajRealizacji", true);

    boType.GetMethod("UstawNabywceWedlugId", F, new[] { typeof(int) })!.Invoke(bo, new object[] { kontrahentId });
    acc.SetProperty(dane, daneType, "DataSprzedazy", DateTime.Now);
    acc.SetProperty(dane, daneType, "DataWydaniaWystawienia", DateTime.Now);

    var poz = boType.GetMethod("Dodaj", F, new[] { typeof(string) })!.Invoke(bo, new object[] { symbol })!;
    acc.SetProperty(poz, poz.GetType(), "Ilosc", 1m);
    var cena = poz.GetType().GetProperty("Cena", F)?.GetValue(poz);
    if (cena is not null)
    {
        acc.SetProperty(cena, cena.GetType(), "BruttoPrzedRabatem", 1.23m);
        acc.SetProperty(cena, cena.GetType(), "BruttoPoRabacie", 1.23m);
        acc.SetProperty(poz, poz.GetType(), "CenaRecznieEdytowana", true);
    }
    acc.InvokeIfExists(bo, boType, "Przelicz");

    // Discover the actual property names before blind-setting - the reflection
    // dump evidence names them "Oddzial" / "StanowiskoKasowe" but that's read
    // from static metadata, not confirmed against THIS Dane entity's shape.
    // On DokumentDS (FV) there is no property literally named "Oddzial" - the only
    // JednostkaOrganizacyjna-typed nav is "MiejsceWprowadzenia" (place-of-entry/branch).
    // Match by TYPE first (authoritative), name-substring as a fallback for other
    // document types that might name it differently.
    var oddzialProp = daneType.GetProperties(F).FirstOrDefault(p => p.PropertyType.Name == "JednostkaOrganizacyjna" && p.CanWrite)
        ?? daneType.GetProperties(F).FirstOrDefault(p => p.Name.Contains("Oddzial", StringComparison.OrdinalIgnoreCase) && p.CanWrite);
    var stanowiskoProp = daneType.GetProperties(F).FirstOrDefault(p => p.PropertyType.Name == "StanowiskoKasowe" && p.CanWrite)
        ?? daneType.GetProperties(F).FirstOrDefault(p => p.Name.Contains("Stanowisko", StringComparison.OrdinalIgnoreCase) && p.CanWrite && p.PropertyType != typeof(Nullable<int>));
    Console.WriteLine($"Dane.Oddzial* writable property: {(oddzialProp is null ? "NOT FOUND" : oddzialProp.Name + " : " + oddzialProp.PropertyType.Name)}");
    Console.WriteLine($"Dane.Stanowisko* writable property: {(stanowiskoProp is null ? "NOT FOUND" : stanowiskoProp.Name + " : " + stanowiskoProp.PropertyType.Name)}");
    if (oddzialProp is null || stanowiskoProp is null)
    {
        foreach (var p in daneType.GetProperties(F))
            Console.WriteLine($"  [Dane prop] {p.PropertyType.Name} {p.Name} {{ {(p.CanRead ? "get; " : "")}{(p.CanWrite ? "set; " : "")}}}");
        log.LogError("Cannot proceed without both properties - see the dump above for the actual names.");
        return;
    }

    // Mirror the proven pattern from SferaDokumentySprzedazyService.ApplyExplicitPayment:
    // set the paired "<Nav>Id" FK property and let EF fixup materialize the nav
    // inside THIS document's own unit of work, instead of resolving via a facade
    // (whose exact type name for Oddzial/Stanowisko is unconfirmed pre-probe).
    bool SetByFkId(string navPropName, int id)
    {
        var idProp = daneType.GetProperty(navPropName + "Id", F);
        if (idProp is null)
        {
            log.LogWarning("Dane.{prop} not found - cannot set {nav} via FK id", navPropName + "Id", navPropName);
            return false;
        }
        idProp.SetValue(dane, id);
        var navProp = daneType.GetProperty(navPropName, F);
        var resolved = navProp?.GetValue(dane);
        var resolvedId = resolved is null ? null : acc.GetProperty(resolved, resolved.GetType(), "Id");
        log.LogInformation("Set {idProp}={id} -> Dane.{nav} resolved to Id={resolvedId}", navPropName + "Id", id, navPropName, resolvedId ?? "NULL (not resolved)");
        return resolved is not null && Equals(resolvedId, id);
    }

    if (setOddzial) SetByFkId(oddzialProp.Name, oddzialId); else log.LogInformation("Skipping Oddzial set (isolation test)");
    if (setStanowisko) SetByFkId(stanowiskoProp.Name, stanowiskoId); else log.LogInformation("Skipping StanowiskoKasowe set (isolation test)");

    // Cash payment: StanowiskoKasoweWymaganeDlaDokumentowZPlatnosciamiNatychmiastowymiBlad
    // says Stanowisko Kasowe matters most for immediate-payment documents.
    var fpId = int.Parse(GetOpt("--fp", "1"));
    ApplyPayment(bo, boType, dane, daneType, new PaymentPlan(Kind: "cash", FormaPlatnosciId: fpId, BankAccountId: null));

    acc.InvokeIfExists(bo, boType, "IgnorujBlokadeRealizacjiPozycji");
    acc.InvokeIfExists(bo, boType, "AutoSymbol");
    acc.InvokeIfExists(bo, boType, "NadajNumer");

    object? saved = null;
    try
    {
        saved = boType.GetMethod("Zapisz", F, Type.EmptyTypes)!.Invoke(bo, null);
    }
    catch (TargetInvocationException tie)
    {
        Console.WriteLine($"RESULT: Zapisz() THREW {tie.InnerException?.GetType().Name}: {tie.InnerException?.Message}");
        return;
    }

    if (saved is true)
    {
        var id = (int)acc.GetProperty(dane, daneType, "Id")!;
        Console.WriteLine($"RESULT: ACCEPTED - saved FV id={id} under oddzial={oddzialId} stanowisko={stanowiskoId}");
    }
    else
    {
        Console.WriteLine($"RESULT: REJECTED - Zapisz()={saved}. " +
            acc.CollectValidationErrors(bo, boType, includeDocumentLevel: true, includeStateHint: true));
    }
}

// Type declarations must come AFTER all top-level statements (including local
// functions) in a top-level-statements file - CS8803 otherwise.
record PaymentPlan(string Kind, int FormaPlatnosciId, int? BankAccountId);
