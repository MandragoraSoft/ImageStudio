using System.Reflection;
using ImageStudio.Composition;
using ImageStudio.Services;
using ImageStudio.Storage;

namespace ImageStudio.Tests;

// Atrapa wtyczki magazynu: operacje rzucają, bo testy odkrywania ich nie wywołują
public abstract class StubStoragePlugin(string id) : IMediaStoragePlugin
{
    public string Id { get; } = id;
    public string DisplayName => id;
    public IReadOnlyList<StorageSetting> Settings => [];
    public void Configure(IReadOnlyDictionary<string, string> values) { }
    public Task<string> WriteAsync(MediaKind kind, string fileName, ReadOnlyMemory<byte> data, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<byte[]> ReadAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> ExistsAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<string> GetLocalPathAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
}

// Konkretna wtyczka z konstruktorem (StoragePluginContext): tak odkrywane są wtyczki chmurowe
public sealed class ContextStoragePlugin(StoragePluginContext context) : StubStoragePlugin("context") { }

// Bez konstruktora z kontekstem: nie jest odkrywana
public sealed class NoContextStoragePlugin : StubStoragePlugin
{
    public NoContextStoragePlugin() : base("nocontext") { }
}

public sealed class FixedIdStoragePlugin(string id) : StubStoragePlugin(id) { }

public class StoragePluginDiscoveryTests
{
    [Fact]
    public void App_assembly_lists_every_storage_library_in_metadata()
    {
        var names = typeof(ServiceCollectionExtensions).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                                                       .Where(a => a.Key == StoragePluginDiscovery.MetadataKey)
                                                       .Select(a => a.Value)
                                                       .ToHashSet();

        Assert.Contains("ImageStudio.Storage.Local", names);
        Assert.Contains("ImageStudio.Storage.OneDrive", names);
        Assert.Contains("ImageStudio.Storage.GoogleDrive", names);
    }

    [Fact]
    public void Container_discovers_cloud_plugins_after_local_in_a_fixed_order()
    {
        using var app = TestApp.Create();

        Assert.Equal(new[] { "local", "googledrive", "onedrive" }, app.Get<MediaStorageHost>().Plugins.Select(p => p.Id));
    }

    [Fact]
    public void Only_concrete_plugins_with_a_context_constructor_are_found()
    {
        var found = StoragePluginDiscovery.PluginTypes(typeof(StoragePluginDiscoveryTests).Assembly).ToList();

        Assert.Contains(typeof(ContextStoragePlugin), found);
        Assert.DoesNotContain(typeof(NoContextStoragePlugin), found);
        Assert.DoesNotContain(typeof(StubStoragePlugin), found);
    }

    [Fact]
    public void Host_ignores_invalid_and_duplicate_ids_and_keeps_the_first()
    {
        var first = new FixedIdStoragePlugin("ok");
        var plugins = new IMediaStoragePlugin[]
        {
            new FixedIdStoragePlugin("Bad-Id"),
            first,
            new FixedIdStoragePlugin("ok"),
            new FixedIdStoragePlugin("a b"),
            new FixedIdStoragePlugin(""),
        };

        var host = new MediaStorageHost(plugins);

        Assert.Equal(new[] { "ok" }, host.Plugins.Select(p => p.Id));
        Assert.Same(first, host.Plugins[0]);
        Assert.Same(first, host.Active);
    }
}
