using ImageStudio.Storage;
using ImageStudio.Storage.Local;

namespace ImageStudio.Tests;

public class MediaStorageTests
{
    [Fact]
    public async Task Local_storage_writes_reads_and_deletes_by_location()
    {
        var dir = Path.Combine(Path.GetTempPath(), "is-media-" + Guid.NewGuid().ToString("N"));
        var storage = new LocalMediaStorage { Roots = { [MediaKind.Video] = dir } };
        try
        {
            var location = await storage.WriteAsync(MediaKind.Video, "../a.mp4", new byte[] { 1, 2 });

            Assert.Equal(Path.Combine(dir, "a.mp4"), location); // nazwa nie wychodzi poza katalog
            Assert.Equal(new byte[] { 1, 2 }, await storage.ReadAsync(location));
            Assert.Equal(location, await storage.GetLocalPathAsync(location));

            await storage.DeleteAsync(location);
            await storage.DeleteAsync(location); // brak pliku nie jest błędem
            Assert.False(await storage.ExistsAsync(location));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
