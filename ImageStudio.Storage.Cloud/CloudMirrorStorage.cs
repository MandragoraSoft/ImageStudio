using System.IO;
using System.Collections.Concurrent;
using ImageStudio.Storage.Local;
using ImageStudio.Networking;

namespace ImageStudio.Storage.Cloud;

// Wspólna część wtyczek chmurowych (OneDrive, Google Drive, MEGA) w modelu lustra: plik trafia najpierw do folderów
// lokalnych (jak w LocalMediaStorage), więc galeria, miniatury i odtwarzanie działają na ścieżkach, a wysyłka do chmury
// idzie w tle po zalogowaniu. Błąd wysyłki nie przerywa generowania: widać go w Status w zakładce Zapis.
// Uwierzytelnianie (OAuth, poświadczenia) jest po stronie klasy pochodnej.
// ponytail: odczyt i usuwanie tylko lokalne (usunięcie kasuje też kopię w chmurze), brak pobierania plików z innego
// komputera; dodać, gdy galeria zacznie czytać przez IMediaStorage
public abstract class CloudMirrorStorage : IMediaStoragePlugin
{
    private readonly ConcurrentBag<Task> _pending = [];
    private readonly object _backgroundGate = new();
    private readonly Dictionary<string, Task> _byFile = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastError;

    protected CloudMirrorStorage(IStorageSecrets secrets) => Secrets = secrets;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string? HelpKey { get; }

    protected IStorageSecrets Secrets { get; }
    public LocalMediaStorage Cache { get; } = new();

    // Zamiana w testach: nie otwiera prawdziwej przeglądarki (czyta ją logowanie OAuth w OAuthCloudStorage)
    public Action<string>? BrowserOverride { get; set; }

    // --- uwierzytelnianie: OAuth (OAuthCloudStorage) albo poświadczenia z sesją ---
    protected abstract bool SignedIn { get; }

    // Kto jest zalogowany (np. e-mail) do statusu; null = sam "Zalogowano."
    protected virtual string? AccountLabel => null;

    // Token albo sesja do operacji w chmurze; null = niezalogowany
    protected abstract Task<string?> AccessTokenAsync(CancellationToken ct);

    protected virtual IEnumerable<StorageSetting> AuthSettings => [];
    protected virtual void ConfigureAuth(IReadOnlyDictionary<string, string> values) { }
    protected virtual string SignInLabelKey => "storage.cloud.signIn";
    protected virtual string? SignInWorkingKey => null;
    protected abstract Task SignInAsync(IReadOnlyDictionary<string, string> inputs, IStorageInteraction interaction, CancellationToken ct);
    protected abstract Task SignOutAsync(CancellationToken ct);

    // --- operacje w chmurze po nazwie pliku ---
    protected abstract Task UploadAsync(string accessToken, string fileName, byte[] data, CancellationToken ct);
    protected abstract Task DeleteRemoteAsync(string accessToken, string fileName, CancellationToken ct);

    // Zawartość pliku z chmury po nazwie; null, gdy go tam nie ma
    protected abstract Task<byte[]?> DownloadRemoteAsync(string accessToken, string fileName, CancellationToken ct);

    public IReadOnlyList<StorageSetting> Settings => [.. AuthSettings, .. Cache.Settings];

    public void Configure(IReadOnlyDictionary<string, string> values)
    {
        ConfigureAuth(values);
        Cache.Configure(values);
    }

    public string? LocalFolder(MediaKind kind) => Cache.LocalFolder(kind);

    public IReadOnlyList<StorageAction> Actions => [new("signin", SignInLabelKey, SignInWorkingKey), new("signout", "storage.cloud.signOut")];

    public bool CanRun(string key) => key switch { "signin" => !SignedIn, "signout" => SignedIn, _ => true };

    public StorageStatus? Status => _lastError is not null ? new("storage.status.error", _lastError)
        : !SignedIn ? new("storage.status.signedOut")
        : AccountLabel is { } account ? new("storage.status.signedInAs", account) : new("storage.status.signedIn");

    public Task RunActionAsync(string key, CancellationToken ct = default) =>
        RunActionAsync(key, new Dictionary<string, string>(), StorageInteraction.None, ct);

    public async Task RunActionAsync(string key, IReadOnlyDictionary<string, string> inputs, IStorageInteraction interaction,
                                     CancellationToken ct = default)
    {
        _lastError = null;
        if (key == "signout") await SignOutAsync(ct);
        else if (key == "signin") await SignInAsync(inputs, interaction, ct);
    }

    public async Task<string> WriteAsync(MediaKind kind, string fileName, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var path = await Cache.WriteAsync(kind, fileName, data, ct);
        var bytes = data.ToArray();
        Background(Path.GetFileName(path), async () =>
        {
            if (await AccessTokenAsync(CancellationToken.None) is { } token)
                await UploadAsync(token, Path.GetFileName(path), bytes, CancellationToken.None);
        });
        return path;
    }

    public async Task DeleteAsync(string location, CancellationToken ct = default)
    {
        await Cache.DeleteAsync(location, ct);
        Background(Path.GetFileName(location), async () =>
        {
            if (await AccessTokenAsync(CancellationToken.None) is { } token)
                await DeleteRemoteAsync(token, Path.GetFileName(location), CancellationToken.None);
        });
    }

    // Czeka na zakończenie wysyłek w tle (testy i zamykanie aplikacji)
    public Task FlushAsync() => Task.WhenAll(_pending);

    private void Background(string fileName, Func<Task> work)
    {
        lock (_backgroundGate)
        {
            _byFile.TryGetValue(fileName, out var previous);
            var task = Task.Run(async () =>
            {
                if (previous is not null) await previous;
                await RunBackgroundAsync(work);
            });
            _byFile[fileName] = task;
            _pending.Add(task);
            _ = task.ContinueWith(_ =>
            {
                lock (_backgroundGate)
                    if (_byFile.GetValueOrDefault(fileName) == task) _byFile.Remove(fileName);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task RunBackgroundAsync(Func<Task> work)
    {
        try
        {
            var delay = TimeSpan.FromMilliseconds(250);
            while (true)
            {
                try
                {
                    await work();
                    break;
                }
                catch (UserCanceledRequestException ex)
                {
                    // Anulowanie dotyczy jednego HTTP, nie zapisanego lokalnie zadania synchronizacji.
                    _lastError = ex.Message;
                    await Task.Delay(delay);
                    delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 30_000));
                }
            }
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }
    }

    // Plik z chmury po nazwie (null: brak pliku albo niezalogowany); używane też do sprawdzenia, co faktycznie wysłano
    public async Task<byte[]?> ReadRemoteAsync(string fileName, CancellationToken ct = default) =>
        await AccessTokenAsync(ct) is { } token ? await DownloadRemoteAsync(token, fileName, ct) : null;

    // Najpierw kopia lokalna; gdy jej nie ma (np. inny komputer, skasowany folder), plik jest pobierany z chmury
    // i odkładany pod tą samą ścieżką lokalną
    public async Task<byte[]> ReadAsync(string location, CancellationToken ct = default)
    {
        if (await Cache.ExistsAsync(location, ct)) return await Cache.ReadAsync(location, ct);

        var bytes = await ReadRemoteAsync(Path.GetFileName(location), ct) ?? throw new FileNotFoundException("Brak pliku lokalnie i w chmurze.", location);
        Directory.CreateDirectory(Path.GetDirectoryName(location)!);
        await File.WriteAllBytesAsync(location, bytes, ct);
        return bytes;
    }

    // Tylko lokalnie: sprawdzenie w chmurze wymagałoby pobrania pliku
    public Task<bool> ExistsAsync(string location, CancellationToken ct = default) => Cache.ExistsAsync(location, ct);

    public async Task<string> GetLocalPathAsync(string location, CancellationToken ct = default)
    {
        if (!await Cache.ExistsAsync(location, ct)) await ReadAsync(location, ct);
        return location;
    }
}
