using System.Collections;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using InsERT.Moria.Sfera;

namespace Subiekt.Bridge.Infrastructure.Sfera;

// The concrete entity classes (Asortyment, Podmiot, ...) live in
// InsERT.Moria.ModelDanych.dll, which we don't reference at compile time.
// We go through reflection to stay loosely coupled.
//
// This holds the type/handle-level reflection helpers (resolve a facade, resolve
// a type by name, flatten an entity for a DTO). The per-entity Get/Set/Invoke
// helpers that used to be duplicated across the 4 services now live in
// SferaObjectAccessor.
public static class SferaReflection
{
    public static object PodajObiektTypu(this Uchwyt uchwyt, Type interfaceType)
    {
        var generic = typeof(Uchwyt)
            .GetMethods()
            .First(m => m.Name == "PodajObiektTypu" && m.IsGenericMethodDefinition);
        var specific = generic.MakeGenericMethod(interfaceType);
        return specific.Invoke(uchwyt, null)!;
    }

    /// <summary>
    /// Load the full sales-document entity (<c>DokumentDS</c>) for a Subiekt document id
    /// via <c>IDokumenty.Znajdz&lt;IDokumentSprzedazy&gt;(d =&gt; d.Id == id)</c> and return
    /// its <c>.Dane</c>. This brittle reflection dance against the unreferenced
    /// <c>InsERT.Moria.Dokumenty.Logistyka</c> assembly lives here once so the issuance/
    /// correction and PDF-render paths share a single copy. Returns null when no such BO
    /// is found OR the resolved BO has no <c>.Dane</c> — both are "document not usable as a
    /// sales document" and callers decide whether that is a 404 or an error. (We return
    /// null rather than throwing on a null <c>.Dane</c> so a non-resolvable document maps to
    /// not-found, not to a generic 422 "rejected" via the adapter's catch-all classifier.)
    /// </summary>
    public static object? LoadDokumentSprzedazyDane(this Uchwyt uchwyt, int documentId)
    {
        var docsType = RequireType("InsERT.Moria.Dokumenty.Logistyka.IDokumenty, InsERT.Moria.Dokumenty.Logistyka");
        var docs = uchwyt.PodajObiektTypu(docsType);
        var znajdzGen = docs.GetType().GetMethods(SferaObjectAccessor.Flags)
            .FirstOrDefault(m => m.Name == "Znajdz" && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name.StartsWith("Expression"))
            ?? throw new InvalidOperationException("IDokumenty.Znajdz<T>(Expression) not found");
        var docBoType = RequireType("InsERT.Moria.Dokumenty.Logistyka.IDokumentSprzedazy, InsERT.Moria.Dokumenty.Logistyka");
        var znajdz = znajdzGen.MakeGenericMethod(docBoType);
        var exprParamType = znajdz.GetParameters()[0].ParameterType;     // Expression<Func<Dokument,bool>>
        var funcType = exprParamType.GetGenericArguments()[0];           // Func<Dokument,bool>
        var entityType = funcType.GetGenericArguments()[0];              // Dokument
        var pe = Expression.Parameter(entityType, "d");
        var predicate = Expression.Lambda(funcType,
            Expression.Equal(Expression.Property(pe, "Id"), Expression.Constant(documentId)), pe);
        var bo = znajdz.Invoke(docs, new object[] { predicate });
        if (bo == null) return null;
        // A null .Dane means the row resolved to a BO without a backing entity — treat it
        // like "not found" (return null) rather than throwing, so the PDF path maps it to
        // 404 instead of the adapter's catch-all 422.
        return bo.GetType().GetProperty("Dane", SferaObjectAccessor.Flags)?.GetValue(bo);
    }

    public static Type RequireType(string fullyQualifiedName)
    {
        // Parse "Namespace.Type, AssemblyName" into the bare type name.
        var parts = fullyQualifiedName.Split(',');
        var typeName = parts[0].Trim();
        var assemblyName = parts.Length >= 2 ? parts[1].Trim() : null;

        // 1) Standard resolution (works if assembly is already loaded by name).
        var type = Type.GetType(fullyQualifiedName, throwOnError: false);
        if (type != null) return type;

        // 2) Search every assembly already loaded into the process. Once Sfera
        //    is connected, all InsERT.* DLLs are in memory — the type's real
        //    assembly may differ from the namespace, so match by full name.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                type = asm.GetType(typeName, throwOnError: false);
                if (type != null) return type;
            }
            catch { /* some dynamic assemblies throw on GetType */ }
        }

        // 3) Force-load the named DLL from the binaries dir via the resolver,
        //    then look again across all assemblies (the type may be forwarded).
        if (assemblyName != null)
        {
            try
            {
                var asm = System.Reflection.Assembly.Load(assemblyName);
                type = asm.GetType(typeName, throwOnError: false);
                if (type != null) return type;
            }
            catch { }
        }

        throw new InvalidOperationException(
            $"Could not load type: {fullyQualifiedName}. " +
            $"Searched {AppDomain.CurrentDomain.GetAssemblies().Length} loaded assemblies.");
    }

    // Projects an entity to a flat dictionary. Skips navigation properties,
    // collections, and deep object graphs — we only keep primitives, strings
    // and nullables. That's enough for a REST DTO and avoids circular EF refs.
    public static Dictionary<string, object?> ToFlatDto(object entity)
    {
        var result = new Dictionary<string, object?>();
        if (entity is null) return result;

        foreach (var p in entity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
            if (!IsSimple(p.PropertyType)) continue;

            object? val;
            try { val = p.GetValue(entity); }
            catch { continue; }

            result[p.Name] = val;
        }
        return result;
    }

    private static bool IsSimple(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsPrimitive
            || t.IsEnum
            || t == typeof(string)
            || t == typeof(decimal)
            || t == typeof(Guid)
            || t == typeof(DateTime)
            || t == typeof(DateTimeOffset)
            || t == typeof(TimeSpan)
            || t == typeof(byte[]);
    }

    public static List<Dictionary<string, object?>> Take(IEnumerable source, int limit)
    {
        var list = new List<Dictionary<string, object?>>(limit);
        foreach (var item in source)
        {
            if (list.Count >= limit) break;
            list.Add(ToFlatDto(item));
        }
        return list;
    }
}
