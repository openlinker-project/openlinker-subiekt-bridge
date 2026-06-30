using System.Reflection;

static class FindTypeEntry
{
    public static int FindType(string[] args)
    {
        if (args.Length < 2 || args[0] != "--find")
        {
            Console.Error.WriteLine("Usage: --find <TypeName>");
            return 2;
        }
        var typeName = args[1];
        var binariesDir = Environment.GetEnvironmentVariable("SFERA_BINARIES")
            ?? @"C:\Users\jakub\AppData\Local\InsERT\Deployments\Nexo\Demo_10a1963f59ca24dc1aed1e4c1d5\Binaries";

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

        var resolver = new PathAssemblyResolver(assemblyPaths);
        using var mlc = new MetadataLoadContext(resolver);

        foreach (var dll in Directory.GetFiles(binariesDir, "InsERT*.dll"))
        {
            Assembly asm;
            try { asm = mlc.LoadFromAssemblyPath(dll); }
            catch { continue; }

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types!.Where(t => t != null).ToArray()!; }
            catch { continue; }

            foreach (var t in types)
            {
                if (t?.Name == typeName || t?.FullName == typeName)
                {
                    var kind = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : "class";
                    Console.WriteLine($"[{kind}] {t.FullName}  in  {Path.GetFileName(dll)}  (asm: {asm.GetName().Name})");
                    if (t.IsEnum)
                    {
                        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                            Console.WriteLine($"    = {f.Name}");
                    }
                }
            }
        }
        return 0;
    }
}