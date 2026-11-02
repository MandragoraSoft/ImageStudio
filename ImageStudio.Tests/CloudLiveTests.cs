using System.Security.Cryptography;
using System.Text;
using ImageStudio.Storage;
using ImageStudio.Storage.Cloud;
using ImageStudio.Storage.GoogleDrive;
using ImageStudio.Storage.OneDrive;

namespace ImageStudio.Tests;

// Fakt uruchamiany tylko z IMAGESTUDIO_LIVE_CLOUD=1: łączy się z prawdziwymi kontami zalogowanymi w ImageStudio
// (Ustawienia -> Zapis), więc w zwykłym przebiegu jest pominięty. Przykład:
//   $env:IMAGESTUDIO_LIVE_CLOUD = "1"; dotnet test ImageStudio.Tests --filter "FullyQualifiedName~CloudLiveTests"
public sealed class LiveCloudFactAttribute : FactAttribute
{
    public LiveCloudFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("IMAGESTUDIO_LIVE_CLOUD") != "1")
            Skip = "Test na żywo: ustaw IMAGESTUDIO_LIVE_CLOUD=1 i zaloguj się w Ustawieniach -> Zapis.";
    }
}

public sealed class CloudLiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "is-live-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // Sekrety z prawdziwego katalogu użytkownika (testy mają własny IMAGESTUDIO_DATA_DIR), ten sam format co SecretStore
    private sealed class UserSecrets : IStorageSecrets
    {
        private static string Path(string name) => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageStudio", $"secrets-{name}.bin");

        public string? Load(string name) => File.Exists(Path(name))
            ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Path(name)), null, DataProtectionScope.CurrentUser))
            : null;

        public void Save(string value, string name)
        {
            if (value == "") File.Delete(Path(name));
            else File.WriteAllBytes(Path(name), ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        }
    }

    // Przykładowe pliki: obraz (prawdziwy PNG 1x1), dźwięk i wideo (nagłówki + losowe bajty)
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static byte[] Sample(string header, int size)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        Encoding.ASCII.GetBytes(header).CopyTo(bytes, 0);
        return bytes;
    }

    private static async Task<byte[]?> WaitForRemoteAsync(CloudMirrorStorage storage, string name, Func<byte[]?, bool> done)
    {
        byte[]? bytes = null;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            bytes = await storage.ReadRemoteAsync(name);
            if (done(bytes)) return bytes;
            await Task.Delay(1000); // listy plików w chmurze bywają opóźnione o chwilę
        }

        return bytes;
    }

    private async Task CrudAsync(CloudMirrorStorage storage)
    {
        Assert.True(storage.Status is { Key: not "storage.status.signedOut" },
                    $"{storage.DisplayName}: zaloguj się w ImageStudio (Ustawienia -> Zapis).");
        foreach (var kind in Enum.GetValues<MediaKind>()) storage.Cache.Roots[kind] = Path.Combine(_dir, kind.ToString());

        var run = Guid.NewGuid().ToString("N")[..8];
        (MediaKind Kind, string Name, byte[] Data)[] files =
        [
            (MediaKind.Image, $"crud-{run}.png", Png),
            (MediaKind.Audio, $"crud-{run}.mp3", Sample("ID3", 4096)),
            (MediaKind.Video, $"crud-{run}.mp4", Sample("ftypmp42", 32 * 1024)),
        ];
        var paths = new List<string>();
        try
        {
            // CREATE: zapis lokalny + wysyłka; plik musi być w chmurze bajt w bajt
            foreach (var (kind, name, data) in files) paths.Add(await storage.WriteAsync(kind, name, data));
            await storage.FlushAsync();
            Assert.True(storage.Status!.Key != "storage.status.error", storage.Status.Detail);
            foreach (var (path, (_, name, data)) in paths.Zip(files))
            {
                Assert.True(File.Exists(path));
                Assert.Equal(data, await WaitForRemoteAsync(storage, name, b => b is not null));
            }

            // READ: po skasowaniu kopii lokalnej plik wraca z chmury i jest odkładany pod tą samą ścieżką
            foreach (var (path, (_, _, data)) in paths.Zip(files))
            {
                File.Delete(path);
                Assert.Equal(data, await storage.ReadAsync(path));
                Assert.Equal(path, await storage.GetLocalPathAsync(path));
                Assert.True(File.Exists(path));
            }

            // UPDATE: ta sama nazwa, nowa zawartość; w chmurze ma być nowa wersja (nie duplikat ze starą)
            var (kind0, name0, _) = files[0];
            var updated = Sample("UPDATED", 2048);
            await storage.WriteAsync(kind0, name0, updated);
            await storage.FlushAsync();
            Assert.Equal(updated, await WaitForRemoteAsync(storage, name0, b => b is not null && b.AsSpan().SequenceEqual(updated)));
        }
        finally
        {
            // DELETE: usunięcie lokalne i zdalne; potem pliku nie ma w chmurze (sprzątamy też po nieudanym teście)
            foreach (var path in paths) await storage.DeleteAsync(path);
            await storage.FlushAsync();
        }

        foreach (var (_, name, _) in files)
            Assert.Null(await WaitForRemoteAsync(storage, name, b => b is null));
        Assert.All(paths, p => Assert.False(File.Exists(p)));
    }

    [LiveCloudFact]
    public Task OneDrive_create_read_update_delete_with_sample_files() => CrudAsync(new OneDriveStorage(new UserSecrets()));

    [LiveCloudFact]
    public Task GoogleDrive_create_read_update_delete_with_sample_files() => CrudAsync(new GoogleDriveStorage(new UserSecrets()));
}
