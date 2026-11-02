using System.IO;
using System.Runtime.Loader;

namespace ImageStudio;

// Biblioteki dostawców, zakładek, grup narzędzi czatu i magazynów leżą w podfolderach providers\, tabs\, tools\ i storage\
// (patrz ImageStudio.csproj); domyślny kontekst ich nie widzi sam. Potrzebne zarówno oknu, jak i trybowi bez okna (CLI).
public static class PluginResolver
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;

        string[] pluginDirs = [Path.Combine(AppContext.BaseDirectory, "providers"), Path.Combine(AppContext.BaseDirectory, "tabs"),
                               Path.Combine(AppContext.BaseDirectory, "tools"), Path.Combine(AppContext.BaseDirectory, "storage")];
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (var dir in pluginDirs)
            {
                var path = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
            }
            return null;
        };
    }
}
