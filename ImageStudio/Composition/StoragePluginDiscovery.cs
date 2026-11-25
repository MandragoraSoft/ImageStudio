using System.Reflection;
using ImageStudio.Services;
using ImageStudio.Storage;
using Microsoft.Extensions.Logging;

namespace ImageStudio.Composition;

// Odkrywanie wtyczek magazynu: nazwy bibliotek z metadanych assembly aplikacji (target AddStoragePluginMetadata
// w ImageStudio.csproj), z nich publiczne typy IMediaStoragePlugin z konstruktorem (StoragePluginContext).
// Porządek stały: po pełnej nazwie typu. LocalMediaStorage nie ma takiego konstruktora: rejestruje ją jawnie
// AddImageStudioCore jako pierwszą (domyślną)
public static class StoragePluginDiscovery
{
    public const string MetadataKey = "ImageStudio.StoragePlugin";
    private static readonly ILogger Log = AppLog.For<MediaStorageHost>();

    public static IReadOnlyList<Type> FindTypes(Assembly app)
    {
        var types = new List<Type>();
        var names = app.GetCustomAttributes<AssemblyMetadataAttribute>()
                       .Where(a => a.Key == MetadataKey && !string.IsNullOrWhiteSpace(a.Value))
                       .Select(a => a.Value!).Distinct(StringComparer.Ordinal);
        foreach (var name in names)
        {
            try
            {
                types.AddRange(PluginTypes(Assembly.Load(new AssemblyName(name))));
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException or ReflectionTypeLoadException)
            {
                // Brak biblioteki (usunięta z storage\) albo jej zależności: aplikacja działa bez tej wtyczki
                Log.LogWarning("Pominięto wtyczkę magazynu {Name}: {Error}", name, ex.Message);
            }
        }
        return types.OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
    }

    public static IEnumerable<Type> PluginTypes(Assembly assembly) =>
        assembly.GetExportedTypes().Where(t => t is { IsClass: true, IsAbstract: false }
                                               && typeof(IMediaStoragePlugin).IsAssignableFrom(t)
                                               && t.GetConstructor([typeof(StoragePluginContext)]) is not null);

    public static IMediaStoragePlugin Create(Type type, StoragePluginContext context) =>
        (IMediaStoragePlugin)Activator.CreateInstance(type, context)!;
}
