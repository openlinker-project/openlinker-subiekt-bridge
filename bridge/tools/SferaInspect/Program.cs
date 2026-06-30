using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;

if (args.Length > 0 && args[0] == "--find")
    return FindTypeEntry.FindType(args);

var binariesDir = Environment.GetEnvironmentVariable("SFERA_BINARIES")
    ?? @"C:\Users\jakub\AppData\Local\InsERT\Deployments\Nexo\Demo_10a1963f59ca24dc1aed1e4c1d5\Binaries";
var targetDlls = args.Length > 1 ? args.Skip(1).ToArray() : new[]
{
    "InsERT.Moria.Sfera.dll",
    "InsERT.Moria.API.dll",
};

var sharedRoot = @"C:\Program Files\dotnet\shared";
var assemblyPaths = new List<string>();
foreach (var pack in new[] { "Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App", "Microsoft.AspNetCore.App" })
{
    var packRoot = Path.Combine(sharedRoot, pack);
    if (!Directory.Exists(packRoot)) continue;
    var latest = Directory.GetDirectories(packRoot).OrderByDescending(x => x).FirstOrDefault();
    if (latest != null) assemblyPaths.AddRange(Directory.GetFiles(latest, "*.dll"));
}
assemblyPaths.AddRange(Directory.GetFiles(binariesDir, "*.dll"));
var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
assemblyPaths = assemblyPaths.Where(p => seen.Add(Path.GetFileName(p))).ToList();

var resolver = new PathAssemblyResolver(assemblyPaths.Distinct());
using var mlc = new MetadataLoadContext(resolver);

var outPath = args.Length > 0 ? args[0] : @"C:\Users\jakub\Desktop\sfera\tools\SferaInspect\api-dump.txt";
using var writer = new StreamWriter(outPath);

foreach (var dll in targetDlls)
{
    var path = Path.Combine(binariesDir, dll);
    writer.WriteLine($"=================================================================");
    writer.WriteLine($"ASSEMBLY: {dll}");
    writer.WriteLine($"=================================================================");
    Assembly asm;
    try { asm = mlc.LoadFromAssemblyPath(path); }
    catch (Exception ex) { writer.WriteLine($"LOAD FAILED: {ex.Message}"); continue; }

    var tfAttr = asm.GetCustomAttributesData()
        .FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.Versioning.TargetFrameworkAttribute");
    writer.WriteLine($"TargetFramework: {tfAttr?.ConstructorArguments.FirstOrDefault().Value}");
    writer.WriteLine();

    Type[] types;
    try { types = asm.GetExportedTypes(); }
    catch (ReflectionTypeLoadException ex) { types = ex.Types!.Where(t => t != null).ToArray()!; }

    foreach (var t in types.OrderBy(x => x.Namespace).ThenBy(x => x.Name))
    {
        try { DumpType(t, writer); }
        catch (Exception ex) { writer.WriteLine($"[ERROR dumping {t.FullName}] {ex.GetType().Name}: {ex.Message}"); writer.WriteLine(); }
    }
}

Console.WriteLine($"Dumped to: {outPath}");
return 0;

static void DumpType(Type t, StreamWriter writer)
{
    var kind = t.IsInterface ? "interface"
             : t.IsEnum ? "enum"
             : t.IsValueType ? "struct"
             : t.IsAbstract && t.IsSealed ? "static class"
             : t.IsAbstract ? "abstract class"
             : "class";
    writer.WriteLine($"[{kind}] {t.FullName}");

    MemberInfo[] members;
    try { members = t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly); }
    catch (Exception ex) { writer.WriteLine($"    [members load failed: {ex.Message}]"); writer.WriteLine(); return; }

    foreach (var m in members.OrderBy(x => x.MemberType).ThenBy(x => x.Name))
    {
        string? line = null;
        try
        {
            line = m switch
            {
                MethodInfo mi when !mi.IsSpecialName =>
                    $"    method {FormatType(mi.ReturnType)} {mi.Name}({string.Join(", ", mi.GetParameters().Select(p => $"{FormatType(p.ParameterType)} {p.Name}"))})",
                PropertyInfo pi =>
                    $"    prop {FormatType(pi.PropertyType)} {pi.Name} {{ {(pi.CanRead ? "get; " : "")}{(pi.CanWrite ? "set; " : "")}}}",
                EventInfo ei =>
                    $"    event {FormatType(ei.EventHandlerType!)} {ei.Name}",
                FieldInfo fi when t.IsEnum && fi.IsStatic =>
                    $"    = {fi.Name}",
                FieldInfo fi =>
                    $"    field {FormatType(fi.FieldType)} {fi.Name}",
                ConstructorInfo ci =>
                    $"    ctor .({string.Join(", ", ci.GetParameters().Select(p => $"{FormatType(p.ParameterType)} {p.Name}"))})",
                _ => null
            };
        }
        catch (Exception ex) { line = $"    [member {m.Name} load failed: {ex.Message}]"; }
        if (line != null) writer.WriteLine(line);
    }
    writer.WriteLine();
}

static string FormatType(Type t)
{
    if (t.IsGenericType)
    {
        var name = t.Name;
        var tick = name.IndexOf('`');
        if (tick > 0) name = name.Substring(0, tick);
        return $"{name}<{string.Join(",", t.GetGenericArguments().Select(FormatType))}>";
    }
    return t.Name;
}