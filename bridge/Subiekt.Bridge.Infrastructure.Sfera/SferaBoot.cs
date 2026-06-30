using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Subiekt.Bridge.Infrastructure.Sfera;

// Dynamic assembly resolver for nexo binaries.
// Must be installed BEFORE any Sfera type is touched — otherwise the default
// loader looks next to our exe and won't find InsERT.* transitive DLLs.
public static class SferaBoot
{
    private static string? _binariesDir;
    private static bool _installed;

    public static void InstallAssemblyResolver(string binariesDir)
    {
        if (_installed) return;
        _binariesDir = binariesDir;

        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            if (_binariesDir is null) return null;
            var path = Path.Combine(_binariesDir, name.Name + ".dll");
            return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
        };

        _installed = true;
    }
}
