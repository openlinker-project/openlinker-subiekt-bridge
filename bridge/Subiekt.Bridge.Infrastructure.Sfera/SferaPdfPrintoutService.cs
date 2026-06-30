using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Subiekt.Bridge.Infrastructure.Sfera;

/// <summary>
/// Renders an issued sales document (FV / PA) to PDF bytes headlessly via the
/// Sfera <c>IWydruk</c> (Stimulsoft) facade — the path proven in spike #2
/// (<c>docs/spikes/sfera-pdf-printout.md</c>). Runs synchronously against a
/// passed-in <see cref="SferaSession"/>; the caller (adapter) serializes it on the
/// single-writer <see cref="SferaWriteQueue"/> so it never races the issue path.
/// <para>
/// Flow: detect FV vs PA from the document number prefix, load the
/// <c>DokumentDS</c> entity via <c>IDokumenty.Znajdz&lt;IDokumentSprzedazy&gt;</c>,
/// create the per-type <c>IWydruk</c> via <c>uchwyt.Wydruki().Utworz(typ)</c>,
/// <c>Inicjalizuj(DokumentDS)</c>, configure <c>ParametryDrukowania</c> for a PDF
/// file export into a private temp dir, <c>EksportAsync()</c> + wait on the handle,
/// read the bytes back, then delete the temp file (always, even on failure).
/// </para>
/// </summary>
public sealed class SferaPdfPrintoutService
{
    // TypWzorcaWydruku enum values (see spike note): FakturaSprzedazy=3000, Paragon=8400.
    private const int TypFakturaSprzedazy = 3000;
    private const int TypParagon = 8400;

    // Bound the render so a stuck Stimulsoft worker can't pin the single write
    // worker forever. Spike measured cold ~3.3s, warm ~0.3–2.1s; 120s is generous.
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(120);

    private readonly ILogger<SferaPdfPrintoutService> _log;
    // Reuse the shared property-setter (primitive conversion + null-guard) instead of a
    // local copy. Same logger so it logs consistently with the rest of this service.
    private readonly SferaObjectAccessor _accessor;

    public SferaPdfPrintoutService(ILogger<SferaPdfPrintoutService> log)
    {
        _log = log;
        _accessor = new SferaObjectAccessor(log);
    }

    /// <summary>Thrown when the requested document id does not exist (maps to 404).</summary>
    public sealed class DocumentNotFoundException : Exception
    {
        public DocumentNotFoundException(int id) : base($"Dokument {id} nie istnieje") { }
    }

    /// <summary>
    /// Thrown when the document exists but the headless print pipeline failed
    /// (no wait handle, no PDF written, bad magic, timeout). These are transient
    /// subsystem glitches, not a client error, so the adapter maps them to
    /// <c>unreachable</c> (503, retryable) rather than <c>rejected</c> (422).
    /// </summary>
    public sealed class RenderFailedException : Exception
    {
        public RenderFailedException(string message) : base(message) { }
    }

    public byte[] Render(SferaSession sfera, int documentId)
    {
        var uchwyt = sfera.Uchwyt;
        var conn = uchwyt.PodajPolaczenie();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();

        // Validate the document exists + detect type (PA vs FS) from its number prefix
        // (same convention as SferaKorektyService). Missing id => 404 via the typed exception.
        string numer;
        using (var ck = conn.CreateCommand())
        {
            ck.CommandText = "SELECT NumerWewnetrzny_PelnaSygnatura FROM ModelDanychContainer.Dokumenty WHERE Id=@id";
            ck.Parameters.AddWithValue("@id", documentId);
            numer = ck.ExecuteScalar() as string ?? throw new DocumentNotFoundException(documentId);
        }
        var isParagon = numer.TrimStart().StartsWith("PA", StringComparison.OrdinalIgnoreCase);
        var typ = isParagon ? TypParagon : TypFakturaSprzedazy;

        // 1. Load the FULL sales document entity (DokumentDS) via the shared loader.
        // A null BO means the row exists in Dokumenty but is not resolvable as a sales
        // document (e.g. a non-sales doc type) — surface that as not_found (404), the
        // same as a missing id, rather than letting a plain InvalidOperationException
        // fall through to the adapter's _ => Classify branch and mis-map to 422.
        var dokumentDs = uchwyt.LoadDokumentSprzedazyDane(documentId)
            ?? throw new DocumentNotFoundException(documentId);

        // 2. Create the per-document printout via the Wydruki() facade.
        var wydruk = CreateWydruk(uchwyt, typ);
        try
        {
            // 3. Initialize with the raw DokumentDS entity (not the BO).
            InvokeRequired(wydruk, "Inicjalizuj", new[] { dokumentDs });

            // Gotcha from the spike (the part that cost the most time): Inicjalizuj only
            // sets the DERIVED field `_dokumentEncja`, but the BASE Wydruk.EksportAsyncBase
            // -> PobierzObiektDoDrukowania pipeline reads the BASE field `_obiektDoWydruku`,
            // which stays null -> EksportAsync throws "Niepoprawny typ obiektu wejściowego".
            // So set the base field reflectively to the DokumentDS entity before rendering.
            SetObiektDoWydruku(wydruk, dokumentDs);

            // 4. Configure PDF file-export into a private temp dir.
            var tempDir = Path.Combine(Path.GetTempPath(), "subiekt-bridge-pdf", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            const string baseName = "doc";
            var exportedPath = Path.Combine(tempDir, baseName + ".pdf");

            try
            {
                ConfigureParametry(wydruk, tempDir, baseName);

                // 5. Render and wait for completion (bounded). The PROVEN render seam
                //    (spike #2 produced real %PDF files this way) is IWydruk.EksportAsync():
                //    with ParametryDrukowania.Eksport=true + FormatEksportu="pdf" +
                //    SciezkaEksportu set, EksportAsync runs the Stimulsoft PDF export on a
                //    background worker and signals the returned EventWaitHandle on completion.
                //    The earlier Drukuj() seam reported success but wrote no file — wrong seam.
                //    The render runs synchronously on the single-writer SferaWriteQueue worker
                //    (the caller serializes it), so no concurrent Sfera access is possible.
                RunRender(wydruk, documentId);

                // Treat a missing/unreadable success flag as failure (fail-closed): only
                // a confirmed true proceeds. `ok == false` would let null (property absent
                // or non-bool) slip through and read a missing/garbage export file.
                var ok = GetBool(wydruk, "OstatniaOperacjaZakonczonaSukcesem");
                if (ok != true)
                    throw new RenderFailedException(
                        $"Sfera PDF render did not confirm success for doc {documentId} " +
                        $"(OstatniaOperacjaZakonczonaSukcesem={ok?.ToString() ?? "null"}): {CollectPrintErrors(wydruk)}");

                if (!File.Exists(exportedPath))
                {
                    // Fall back to whatever single file landed in the dir (defensive: the
                    // exporter may sanitize the user filename or extension differently).
                    exportedPath = Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories).FirstOrDefault()
                        ?? throw new RenderFailedException(
                            $"Sfera reported success but no file was written for doc {documentId}. " +
                            $"Effective params: Eksport={ReadProp(wydruk, "Eksport")}, " +
                            $"FormatEksportu={ReadProp(wydruk, "FormatEksportu")}, " +
                            $"SciezkaEksportu={ReadProp(wydruk, "SciezkaEksportu")}, " +
                            $"NazwaDokumentuUzytkownika={ReadProp(wydruk, "NazwaDokumentuUzytkownika")}.");
                }

                var bytes = File.ReadAllBytes(exportedPath);
                if (bytes.Length < 4 || bytes[0] != (byte)'%' || bytes[1] != (byte)'P'
                    || bytes[2] != (byte)'D' || bytes[3] != (byte)'F')
                    throw new RenderFailedException($"Exported file for doc {documentId} is not a PDF (bad magic).");

                _log.LogInformation("Rendered doc {id} ({typ}) to PDF, {bytes} bytes",
                    documentId, isParagon ? "PA" : "FV", bytes.Length);
                return bytes;
            }
            finally
            {
                // Always clean up the whole per-render temp dir.
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
                catch (Exception ex) { _log.LogWarning("Temp PDF cleanup failed for {dir}: {e}", tempDir, ex.Message); }
            }
        }
        finally
        {
            // Dispose the IWydruk / its handles if it is disposable.
            if (wydruk is IDisposable d) { try { d.Dispose(); } catch { /* best-effort */ } }
        }
    }

    // ---- Sfera reflection helpers (mirror the conventions in SferaKorektyService) ----

    private static object CreateWydruk(InsERT.Moria.Sfera.Uchwyt uchwyt, int typValue)
    {
        // uchwyt.Wydruki() is an extension on InsERT.Moria.Sfera.UchwytRozszerzenia.
        // There are multiple "Wydruki" overloads (GetMethod by name alone throws
        // AmbiguousMatchException), so pick the single-parameter (Uchwyt) overload.
        var rozszerzenia = SferaReflection.RequireType("InsERT.Moria.Sfera.UchwytRozszerzenia, InsERT.Moria.Wydruki");
        var uchwytType = uchwyt.GetType();
        var wydrukiExt = rozszerzenia.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Wydruki"
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType.IsAssignableFrom(uchwytType))
            ?? throw new InvalidOperationException("UchwytRozszerzenia.Wydruki(Uchwyt) not found");
        var wydruki = wydrukiExt.Invoke(null, new object[] { uchwyt })
            ?? throw new InvalidOperationException("uchwyt.Wydruki() returned null");

        // IWydruki.Utworz(TypWzorcaWydruku typ) -> IWydruk.
        var typEnum = SferaReflection.RequireType("InsERT.Moria.Wydruki.Enums.TypWzorcaWydruku, InsERT.Moria.Wydruki");
        var typVal = Enum.ToObject(typEnum, typValue);
        var utworz = wydruki.GetType().GetMethods(SferaObjectAccessor.Flags)
            .FirstOrDefault(m => m.Name == "Utworz" && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typEnum)
            ?? throw new InvalidOperationException("IWydruki.Utworz(TypWzorcaWydruku) not found");
        return utworz.Invoke(wydruki, new object[] { typVal })
            ?? throw new InvalidOperationException("IWydruki.Utworz returned null");
    }

    private void ConfigureParametry(object wydruk, string exportDir, string baseName)
    {
        var parametry = wydruk.GetType().GetProperty("ParametryDrukowania", SferaObjectAccessor.Flags)?.GetValue(wydruk)
            ?? throw new InvalidOperationException("IWydruk.ParametryDrukowania is null");
        var pType = parametry.GetType();

        _accessor.SetProperty(parametry, pType, "Eksport", true);
        _accessor.SetProperty(parametry, pType, "Drukowac", false);
        _accessor.SetProperty(parametry, pType, "Email", false);
        _accessor.SetProperty(parametry, pType, "FormatEksportu", "pdf");
        _accessor.SetProperty(parametry, pType, "SciezkaEksportu", exportDir);   // directory, not a file path
        _accessor.SetProperty(parametry, pType, "NazwaDokumentuUzytkownika", baseName);
        _accessor.SetProperty(parametry, pType, "ZastapPliki", true);
        // WybranyWzorzec is already the document's default template after Inicjalizuj —
        // do NOT override it (relies on the Nexo DB's per-doc default; see spike note).

        var formaty = pType.GetProperty("DostepneFormatyEksportu", SferaObjectAccessor.Flags)?.GetValue(parametry);
        var formatyStr = formaty is System.Collections.IEnumerable en
            ? string.Join(",", en.Cast<object?>().Select(x => x?.ToString()))
            : formaty?.ToString() ?? "(null)";
        _log.LogInformation("Print params set: Eksport={e} Drukowac={d} FormatEksportu={f} DostepneFormaty=[{fl}] WybranyWzorzec={w}",
            ReadProp(wydruk, "Eksport"), ReadProp(wydruk, "Drukowac"), ReadProp(wydruk, "FormatEksportu"),
            formatyStr, ReadProp(wydruk, "WybranyWzorzec"));
    }

    // The spike's gotcha: Inicjalizuj only sets the derived `_dokumentEncja`, leaving the
    // BASE `_obiektDoWydruku` (read by the EksportAsync pipeline) null. Set it to the
    // DokumentDS entity unconditionally before rendering, else EksportAsync throws
    // "Niepoprawny typ obiektu wejściowego". The field lives on a base type, so walk up.
    private void SetObiektDoWydruku(object wydruk, object dokumentDs)
    {
        var field = FindField(wydruk.GetType(), "_obiektDoWydruku")
            ?? throw new RenderFailedException(
                $"Base field _obiektDoWydruku not found on {wydruk.GetType().Name} or its bases; " +
                "the Sfera SDK print pipeline shape may have changed.");
        field.SetValue(wydruk, dokumentDs);
        _log.LogInformation("Set base _obiektDoWydruku = {t} before EksportAsync", dokumentDs.GetType().Name);
    }

    private static FieldInfo? FindField(Type? type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f != null) return f;
        }
        return null;
    }

    // Match a reflected method name to a simple name, allowing an explicit-interface
    // prefix (e.g. "InsERT.Moria.Wydruki.IWydruk.EksportAsync" matches "EksportAsync").
    // Exact tail match on the dotted segment so "EksportAsyncBase" does NOT match "Eksport".
    private static bool NameMatches(string reflectedName, string simpleName)
    {
        if (reflectedName == simpleName) return true;
        var dot = reflectedName.LastIndexOf('.');
        return dot >= 0 && reflectedName.AsSpan(dot + 1).SequenceEqual(simpleName);
    }

    // Collect public + non-public instance methods declared anywhere in the type's
    // hierarchy. GetMethods does not return non-public members inherited from a base
    // type, so we walk BaseType explicitly and gather each level's DeclaredOnly methods.
    private static List<MethodInfo> AllMethods(Type? type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public
            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var methods = new List<MethodInfo>();
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            methods.AddRange(t.GetMethods(flags));
        return methods;
    }

    private static object? InvokeRequired(object target, string method, object[] args)
    {
        var m = target.GetType().GetMethods(SferaObjectAccessor.Flags)
            .FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length)
            ?? throw new InvalidOperationException($"{target.GetType().Name}.{method}({args.Length} args) not found");
        return m.Invoke(target, args);
    }

    // Render via IWydruk.EksportAsync() and block (bounded) on the returned EventWaitHandle.
    // Runs directly on the single write-queue worker (which holds the session lock), so
    // single-writer serialization is preserved — no concurrent Sfera access. The spike
    // proved no STA apartment is needed for EksportAsync (plain worker thread is fine).
    private void RunRender(object wydruk, int documentId)
    {
        var exportResult = InvokeEksportAsync(wydruk);
        if (exportResult is WaitHandle handle)
        {
            if (!handle.WaitOne(RenderTimeout))
                throw new RenderFailedException(
                    $"Sfera PDF render timed out after {RenderTimeout.TotalSeconds:0}s for doc {documentId}");
        }
        else if (exportResult != null)
        {
            // Some SDK builds expose EksportAsync as a Task; await it bounded if so.
            if (exportResult is System.Threading.Tasks.Task task)
            {
                if (!task.Wait(RenderTimeout))
                    throw new RenderFailedException(
                        $"Sfera PDF render task timed out after {RenderTimeout.TotalSeconds:0}s for doc {documentId}");
            }
        }
        // A null/void return is acceptable: a synchronous variant just completed inline.
    }

    // Invoke the proven render seam: IWydruk.EksportAsync(). On the live SDK this is the
    // parameterless export entry that returns an EventWaitHandle signalled on completion.
    // With ParametryDrukowania.Eksport=true + FormatEksportu="pdf" + SciezkaEksportu set,
    // it writes a PDF file (Stimulsoft file output) and signals when done.
    private object? InvokeEksportAsync(object wydruk)
    {
        var t = wydruk.GetType();
        // EksportAsync() is an EXPLICIT interface implementation of IWydruk on the base
        // Wydruk`2 — its reflected Name is the fully-qualified
        // "InsERT.Moria.Wydruki.IWydruk.EksportAsync" (IsPrivate=true), NOT "EksportAsync".
        // So match the parameterless method whose name is (or ends with) ".EksportAsync".
        // Fall back to the parameterless explicit-interface Eksport() (sync; same work).
        var all = AllMethods(t);
        var candidate =
            all.FirstOrDefault(m => m.GetParameters().Length == 0 && NameMatches(m.Name, "EksportAsync"))
            ?? all.FirstOrDefault(m => m.GetParameters().Length == 0 && NameMatches(m.Name, "Eksport"));

        if (candidate == null)
        {
            var sigs = all
                .Where(m => m.Name.Contains("Eksport") || m.Name.Contains("Drukuj"))
                .Select(m => $"{m.DeclaringType?.Name}.{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");
            throw new RenderFailedException($"No invocable EksportAsync/Eksport on {t.Name}. Saw: {string.Join(" | ", sigs)}");
        }
        _log.LogInformation("Render seam resolved: {decl}.{name}({ps})",
            candidate.DeclaringType?.Name, candidate.Name,
            string.Join(",", candidate.GetParameters().Select(p => p.ParameterType.Name)));

        var ps = candidate.GetParameters();
        var args = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            if (ps[i].HasDefaultValue) args[i] = ps[i].DefaultValue;
            else if (ps[i].IsOptional) args[i] = Type.Missing;
            else
            {
                var pt = ps[i].ParameterType;
                args[i] = pt.IsValueType && Nullable.GetUnderlyingType(pt) == null
                    ? Activator.CreateInstance(pt)
                    : null;
            }
        }

        _log.LogInformation("Render via {sig}", $"{candidate.Name}({string.Join(",", ps.Select(p => p.ParameterType.Name))})");
        return candidate.Invoke(wydruk, BindingFlags.OptionalParamBinding | SferaObjectAccessor.Flags, null, args, null);
    }

    // Read a ParametryDrukowania property back (for failure diagnostics).
    private static string ReadProp(object wydruk, string prop)
    {
        try
        {
            var parametry = wydruk.GetType().GetProperty("ParametryDrukowania", SferaObjectAccessor.Flags)?.GetValue(wydruk);
            if (parametry == null) return "(no ParametryDrukowania)";
            var v = parametry.GetType().GetProperty(prop, SferaObjectAccessor.Flags)?.GetValue(parametry);
            return v?.ToString() ?? "(null)";
        }
        catch (Exception ex) { return $"(read failed: {ex.Message})"; }
    }

    private static bool? GetBool(object target, string prop)
    {
        var v = target.GetType().GetProperty(prop, SferaObjectAccessor.Flags)?.GetValue(target);
        return v as bool?;
    }

    private static string CollectPrintErrors(object wydruk)
    {
        try
        {
            var m = wydruk.GetType().GetMethod("PobierzListeBledow", SferaObjectAccessor.Flags, Type.EmptyTypes);
            if (m?.Invoke(wydruk, null) is System.Collections.IEnumerable errs)
                return string.Join(" | ", errs.Cast<object?>().Where(e => e != null).Select(e => e!.ToString()));
        }
        catch { /* best-effort */ }
        return "(no error detail exposed)";
    }
}
