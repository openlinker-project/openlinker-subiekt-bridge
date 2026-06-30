using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Subiekt.Bridge.Infrastructure.Sfera;

/// <summary>
/// Single home for the entity-level reflection helpers that used to be copy-pasted
/// into each of the four Sfera services (PodmiotyService, DokumentySprzedazyService,
/// AsortymentyService, KorektyService). Behaviour is the union of those copies:
/// <list type="bullet">
/// <item><see cref="SetProperty"/> — set a property with primitive type conversion. Logs the
/// property NAME at Information and the value only at Debug (the value may carry PII
/// — NIP/nazwa/adres — or commercial prices), matching the stricter Dokumenty copy.</item>
/// <item><see cref="GetProperty"/> — read a property, swallowing reflection errors.</item>
/// <item><see cref="SetBoolFlag"/> — set a writable bool flag (block bypass flags).</item>
/// <item><see cref="InvokeIfExists"/> — invoke a parameterless method if present, best-effort.</item>
/// <item><see cref="NavTo"/> — read a navigation property, null on miss.</item>
/// <item><see cref="CollectValidationErrors"/> — pull human-readable broken-rule text off a
/// rejected BO (InvalidData + document-level warning/message collections).</item>
/// <item><see cref="ExtractEntityErrors"/> — per-field broken rules via IDataErrorInfo +
/// INotifyDataErrorInfo.</item>
/// <item><see cref="DumpShape"/> — log an entity's writable property names (shape discovery).</item>
/// </list>
/// The accessor carries an <see cref="ILogger"/> so the moved services keep logging
/// exactly as before; pass the service's own logger.
/// </summary>
public sealed class SferaObjectAccessor
{
    // IgnoreCase keeps the legacy lenient property matching ("StawkaVatId" etc.).
    public const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

    private readonly ILogger _log;

    public SferaObjectAccessor(ILogger log) => _log = log;

    /// <summary>
    /// Set <paramref name="propertyName"/> on <paramref name="entity"/>, converting
    /// the value to the (possibly nullable) target type when needed. No-op + warn when
    /// the property is missing or read-only.
    /// </summary>
    public void SetProperty(object entity, Type type, string propertyName, object? value)
    {
        var prop = type.GetProperty(propertyName, Flags);
        if (prop == null) { _log.LogWarning("Property '{p}' not found on {t}", propertyName, type.Name); return; }
        if (!prop.CanWrite) { _log.LogWarning("Property '{p}' on {t} is read-only", propertyName, type.Name); return; }
        try
        {
            // Convert primitives where the target type differs (e.g. bool stored as Boolean).
            var target = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            var converted = (value != null && target != value.GetType() && !target.IsInstanceOfType(value))
                ? Convert.ChangeType(value, target)
                : value;
            prop.SetValue(entity, converted);
            // PII/commercial-sensitive: log only the property name at Information.
            _log.LogInformation("Set {t}.{p}", type.Name, propertyName);
            _log.LogDebug("Set {t}.{p} = {v}", type.Name, propertyName, value);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to set {t}.{p}: {e}", type.Name, propertyName, ex.Message);
        }
    }

    /// <summary>Read a property value, returning null on any reflection failure.</summary>
    public object? GetProperty(object entity, Type type, string propertyName)
    {
        try { return type.GetProperty(propertyName, Flags)?.GetValue(entity); }
        catch { return null; }
    }

    /// <summary>Set a writable bool flag (e.g. block-bypass flags); no-op when missing/readonly.</summary>
    public void SetBoolFlag(object obj, Type type, string propertyName, bool value)
    {
        var prop = type.GetProperty(propertyName, Flags);
        if (prop?.CanWrite != true) return;
        try { prop.SetValue(obj, value); _log.LogInformation("Set flag {p} = {v}", propertyName, value); }
        catch (Exception ex) { _log.LogWarning("Failed to set flag {p}: {e}", propertyName, ex.Message); }
    }

    /// <summary>Invoke a parameterless method if it exists; best-effort (warn on failure).</summary>
    public void InvokeIfExists(object obj, Type type, string methodName)
    {
        var m = type.GetMethod(methodName, Flags, Type.EmptyTypes);
        if (m == null) return;
        try { m.Invoke(obj, null); _log.LogInformation("{m}() called", methodName); }
        catch (Exception ex) { _log.LogWarning("{m}() failed: {e}", methodName, ex.Message); }
    }

    /// <summary>Read a navigation property by name (null if missing or null value).</summary>
    public object? NavTo(object obj, string propName)
    {
        try { return obj.GetType().GetProperty(propName, Flags)?.GetValue(obj); }
        catch { return null; }
    }

    /// <summary>Log an object's type + writable property names — shape discovery for Sfera entities.</summary>
    public void DumpShape(string label, object obj)
    {
        try
        {
            var t = obj.GetType();
            var props = t.GetProperties(Flags).Where(p => p.CanWrite).Select(p => p.Name);
            _log.LogInformation("SHAPE {label}: {type} :: {props}", label, t.FullName, string.Join(", ", props));
        }
        catch (Exception ex) { _log.LogWarning("DumpShape {label} failed: {e}", label, ex.Message); }
    }

    /// <summary>
    /// Pull human-readable validation messages off a rejected BO so a Zapisz()==false
    /// surfaces a reason instead of just "false". Includes per-field broken rules from
    /// InvalidData and, when <paramref name="includeDocumentLevel"/> is true, the
    /// document-level warning/message/error collections (Ostrzezenia/Komunikaty/Bledy/
    /// Blokada/Realizacja) where stock/rozchód blocks surface. When nothing is exposed
    /// and <paramref name="includeStateHint"/> is true, append a State/MoznaZapisac hint.
    /// </summary>
    public string CollectValidationErrors(object bo, Type boType, bool includeDocumentLevel = false, bool includeStateHint = false)
    {
        var msgs = new List<string>();

        // 1. Per-field broken rules from InvalidData entities.
        try
        {
            var invalid = boType.GetProperty("InvalidData", Flags)?.GetValue(bo);
            if (invalid is System.Collections.IEnumerable entities)
                foreach (var entity in entities)
                    if (entity != null) ExtractEntityErrors(entity, msgs);
        }
        catch (Exception ex) { msgs.Add("(error reading InvalidData: " + ex.Message + ")"); }

        // 2. Document-level warning/message/error collections (where blocks like
        //    "brak na stanie"/realizacja surface when Zapisz()==false w/o field errors).
        if (includeDocumentLevel)
        {
            try
            {
                foreach (var p in boType.GetProperties(Flags))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    var n = p.Name;
                    if (!(n.Contains("strzez") || n.Contains("Ostrzez") || n.Contains("Komunikat")
                          || n.Contains("Blad") || n.Contains("Blok") || n.Contains("Realizac")))
                        continue;
                    object? val;
                    try { val = p.GetValue(bo); } catch { continue; }
                    if (val is string sv) { if (!string.IsNullOrWhiteSpace(sv)) msgs.Add($"{n}: {sv}"); }
                    else if (val is System.Collections.IEnumerable en and not string)
                    {
                        foreach (var it in en)
                        {
                            if (it == null) continue;
                            var s = (it.GetType().GetProperty("Tekst", Flags)?.GetValue(it)
                                     ?? it.GetType().GetProperty("Komunikat", Flags)?.GetValue(it)
                                     ?? it.GetType().GetProperty("Opis", Flags)?.GetValue(it)
                                     ?? it)?.ToString();
                            if (!string.IsNullOrWhiteSpace(s)) msgs.Add($"{n}: {s}");
                        }
                    }
                }
            }
            catch { /* best-effort */ }
        }

        if (msgs.Count > 0) return "Reasons: " + string.Join(" | ", msgs.Distinct());

        if (includeStateHint)
        {
            // No message exposed — surface the state hints. A Zapisz==false with
            // MoznaZapisac==true is typically a stock/rozchód block Subiekt does
            // not expose as text through the BO.
            object? mozna = null, state = null;
            try { mozna = boType.GetProperty("MoznaZapisac", Flags)?.GetValue(bo); } catch { }
            try { state = boType.GetProperty("State", Flags)?.GetValue(bo); } catch { }
            return $"Subiekt odrzucił zapis (Zapisz=false, MoznaZapisac={mozna ?? "?"}, State={state ?? "?"}) " +
                   "bez szczegółowego komunikatu — typowo blokada rozchodu/stanu magazynowego.";
        }

        return "No validation detail available.";
    }

    /// <summary>
    /// Per-field broken-rule extraction. Mox surfaces per-field validation through the
    /// IDataErrorInfo string indexer (this[propName]) and INotifyDataErrorInfo.GetErrors;
    /// entity-level checks return through IDataErrorInfo.Error.
    /// </summary>
    public void ExtractEntityErrors(object entity, List<string> msgs)
    {
        var type = entity.GetType();
        var label = type.Name;

        if (entity is System.ComponentModel.IDataErrorInfo dei)
        {
            try { if (!string.IsNullOrWhiteSpace(dei.Error)) msgs.Add($"{label}: {dei.Error}"); } catch { }
        }

        var indexer = type.GetProperty("Item", typeof(string), new[] { typeof(string) });
        var ndei = entity as System.ComponentModel.INotifyDataErrorInfo;

        foreach (var p in type.GetProperties(Flags))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            var name = p.Name;

            if (indexer != null)
            {
                try
                {
                    if (indexer.GetValue(entity, new object[] { name }) is string err && !string.IsNullOrWhiteSpace(err))
                        msgs.Add($"{label}.{name}: {err}");
                }
                catch { }
            }

            if (ndei != null)
            {
                try
                {
                    var errs = ndei.GetErrors(name);
                    if (errs != null)
                        foreach (var er in errs)
                            if (er != null) msgs.Add($"{label}.{name}: {er}");
                }
                catch { }
            }
        }
    }
}
