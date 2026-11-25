using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.Storage;

namespace ImageStudio.Tests;

// Atrapa magazynu w pamięci: pliki po lokalizacji, opcjonalne błędy zapisu i kasowania
public sealed class MemoryStoragePlugin(string id) : IMediaStoragePlugin
{
    public Dictionary<string, byte[]> Files { get; init; } = [];
    public string Folder { get; init; } = id;
    public bool FailWrites { get; set; }
    public bool FailDeletes { get; set; }
    public MediaKind? LastKind { get; private set; }

    public string Id { get; } = id;
    public string DisplayName => Id;
    public IReadOnlyList<StorageSetting> Settings => [];
    public void Configure(IReadOnlyDictionary<string, string> values) { }

    public Task<string> WriteAsync(MediaKind kind, string fileName, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (FailWrites) throw new IOException("zapis nieudany");
        LastKind = kind;
        var location = $"{Folder}/{fileName}";
        Files[location] = data.ToArray();
        return Task.FromResult(location);
    }

    public Task<byte[]> ReadAsync(string location, CancellationToken ct = default) => Task.FromResult(Files[location]);
    public Task<bool> ExistsAsync(string location, CancellationToken ct = default) => Task.FromResult(Files.ContainsKey(location));

    public Task DeleteAsync(string location, CancellationToken ct = default)
    {
        if (FailDeletes) throw new IOException("kasowanie nieudane");
        Files.Remove(location);
        return Task.CompletedTask;
    }

    public Task<string> GetLocalPathAsync(string location, CancellationToken ct = default) => Task.FromResult(location);
}

// Właściciel wpisu galerii: operacje na pliku idą do wtyczki, która go zapisała, nie do aktywnej
public sealed class MediaStorageHostTests
{
    private static GalleryEntry Entry(string kind, string? storageId, string filePath) =>
        new() { Kind = kind, StorageId = storageId, FilePath = filePath };

    [Fact]
    public async Task Move_writes_to_target_then_moves_entry_and_deletes_old_copy()
    {
        var local = new MemoryStoragePlugin("local");
        var cloud = new MemoryStoragePlugin("cloud");
        local.Files["local/a.png"] = [1, 2, 3];
        var host = new MediaStorageHost([local, cloud]);
        var entry = Entry("generate", "local", "local/a.png");

        await host.MoveAsync(entry, cloud);

        Assert.Equal("cloud", entry.StorageId);
        Assert.Equal("cloud/a.png", entry.FilePath);
        Assert.Equal([1, 2, 3], cloud.Files["cloud/a.png"]);
        Assert.Equal(MediaKind.Image, cloud.LastKind);
        Assert.DoesNotContain("local/a.png", local.Files.Keys);
    }

    // Lustro chmury w tym samym folderze co dysk lokalny: zapis nadpisuje ten sam plik, więc kasowanie "starej kopii" zniszczyłoby wynik
    [Fact]
    public async Task Move_keeps_the_file_when_the_target_writes_to_the_same_location()
    {
        var shared = new Dictionary<string, byte[]> { ["pics/a.png"] = [1, 2, 3] };
        var local = new MemoryStoragePlugin("local") { Files = shared, Folder = "pics" };
        var cloud = new MemoryStoragePlugin("cloud") { Files = shared, Folder = "pics" };
        var host = new MediaStorageHost([local, cloud]);
        var entry = Entry("generate", "local", "pics/a.png");

        await host.MoveAsync(entry, cloud);

        Assert.Equal("cloud", entry.StorageId);
        Assert.Equal([1, 2, 3], shared["pics/a.png"]);
    }

    [Fact]
    public async Task Move_video_is_written_as_video()
    {
        var local = new MemoryStoragePlugin("local");
        var cloud = new MemoryStoragePlugin("cloud");
        local.Files["local/v.mp4"] = [9];
        var host = new MediaStorageHost([local, cloud]);

        await host.MoveAsync(Entry("video", "local", "local/v.mp4"), cloud);

        Assert.Equal(MediaKind.Video, cloud.LastKind);
    }

    [Fact]
    public async Task Failed_write_leaves_entry_and_old_copy_untouched()
    {
        var local = new MemoryStoragePlugin("local");
        var cloud = new MemoryStoragePlugin("cloud") { FailWrites = true };
        local.Files["local/a.png"] = [1];
        var host = new MediaStorageHost([local, cloud]);
        var entry = Entry("edit", "local", "local/a.png");

        await Assert.ThrowsAsync<IOException>(() => host.MoveAsync(entry, cloud));

        Assert.Equal("local", entry.StorageId);
        Assert.Equal("local/a.png", entry.FilePath);
        Assert.Contains("local/a.png", local.Files.Keys);
    }

    [Fact]
    public async Task Failed_delete_of_old_copy_does_not_undo_the_move()
    {
        var local = new MemoryStoragePlugin("local") { FailDeletes = true };
        var cloud = new MemoryStoragePlugin("cloud");
        local.Files["local/a.png"] = [1];
        var host = new MediaStorageHost([local, cloud]);
        var entry = Entry("generate", "local", "local/a.png");

        await host.MoveAsync(entry, cloud);

        Assert.Equal("cloud", entry.StorageId);
        Assert.Contains("cloud/a.png", cloud.Files.Keys);
    }

    [Fact]
    public async Task Transcript_and_same_storage_are_rejected()
    {
        var local = new MemoryStoragePlugin("local");
        var cloud = new MemoryStoragePlugin("cloud");
        var host = new MediaStorageHost([local, cloud]);

        await Assert.ThrowsAsync<ArgumentException>(() => host.MoveAsync(Entry("transcript", "local", "local/t.txt"), cloud));
        await Assert.ThrowsAsync<ArgumentException>(() => host.MoveAsync(Entry("generate", "cloud", "cloud/a.png"), cloud));
        await Assert.ThrowsAsync<ArgumentException>(() => host.MoveAsync(Entry("generate", null, "local/a.png"), local));
    }

    [Fact]
    public void Owner_is_the_plugin_that_wrote_the_entry()
    {
        var local = new FixedIdStoragePlugin("local");
        var cloud = new FixedIdStoragePlugin("cloud");
        var host = new MediaStorageHost([local, cloud]);
        host.Apply("cloud", null);

        Assert.Same(cloud, host.Owner("cloud"));
        Assert.Same(local, host.Owner("local"));
        Assert.Same(cloud, host.Active);
    }

    [Fact]
    public void Legacy_and_unknown_entries_belong_to_local()
    {
        var cloud = new FixedIdStoragePlugin("cloud");
        var local = new FixedIdStoragePlugin("local");
        var host = new MediaStorageHost([cloud, local]); // local nie jest pierwsza, a aktywna jest cloud

        Assert.Same(local, host.Owner(null));
        Assert.Same(local, host.Owner(""));
        Assert.Same(local, host.Owner("zniknela"));
    }
}
