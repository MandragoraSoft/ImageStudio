using System.IO;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using ImageStudio.Storage.Local;

namespace ImageStudio.Storage.Cloud;

// Wspólna część wtyczek chmurowych (OneDrive, Google Drive) w modelu lustra: plik trafia najpierw do folderów
// lokalnych (jak w LocalMediaStorage), więc galeria, miniatury i odtwarzanie działają na ścieżkach, a wysyłka do chmury
// idzie w tle po zalogowaniu. Błąd wysyłki nie przerywa generowania: widać go w Status w zakładce Zapis.
// ponytail: odczyt i usuwanie tylko lokalne (usunięcie kasuje też kopię w chmurze), brak pobierania plików z innego
// komputera; dodać, gdy galeria zacznie czytać przez IMediaStorage
public abstract class CloudMirrorStorage : IMediaStoragePlugin
{
    private readonly ConcurrentBag<Task> _pending = [];
    private readonly Func<string, string?> _env;
    private string? _lastError;

    // environment: odczyt zmiennych środowiska (testy podstawiają własny); domyślnie Environment
    protected CloudMirrorStorage(IStorageSecrets secrets, HttpMessageHandler? handler = null, Func<string, string?>? environment = null)
    {
        Secrets = secrets;
        _env = environment ?? Environment.GetEnvironmentVariable;
        Http = handler is null ? new HttpClient { Timeout = TimeSpan.FromMinutes(10) } : new HttpClient(handler);
        Flow = new OAuthFlow(Http, OpenBrowser);
    }

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string? HelpKey { get; }
    protected abstract OAuthEndpoints Endpoints { get; }

    // Google wymaga sekretu klienta także od aplikacji desktopowej; Microsoft nie
    protected virtual bool UsesClientSecret => false;

    // Zmienne środowiska (np. z pliku .env) z domyślnym Client ID i sekretem; używane, gdy w ustawieniach pole jest puste
    protected abstract string EnvClientIdVar { get; }
    protected virtual string? EnvClientSecretVar => null;

    protected abstract Task UploadAsync(string accessToken, string fileName, byte[] data, CancellationToken ct);
    protected abstract Task DeleteRemoteAsync(string accessToken, string fileName, CancellationToken ct);

    protected IStorageSecrets Secrets { get; }
    protected HttpClient Http { get; }

    // Zamiana w testach: nie otwiera prawdziwej przeglądarki
    public Action<string>? BrowserOverride { get; set; }
    private void OpenBrowser(string url) => (BrowserOverride ?? (u => System.Diagnostics.Process.Start(
        new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true })))(url);

    public OAuthFlow Flow { get; }
    public LocalMediaStorage Cache { get; } = new();
    // Client ID też leży w sekretach (nie w state.json): nie jest tajny wobec Google, ale nie ma po co leżeć jawnie
    public string ClientId => Secrets.Load(ClientIdName) ?? "";

    // Wartość użyta do logowania: zapisana w ustawieniach, a gdy jej brak, ze zmiennej środowiska
    private string EffectiveClientId => Fallback(ClientId, EnvClientIdVar);
    private string? EffectiveClientSecret => UsesClientSecret ? Fallback(Secrets.Load(ClientSecretName), EnvClientSecretVar) : null;

    private string Fallback(string? saved, string? envVar) =>
        !string.IsNullOrWhiteSpace(saved) ? saved : envVar is not null && _env(envVar) is { Length: > 0 } env ? env.Trim() : "";

    private string TokenName => $"storage-{Id}";
    private string ClientSecretName => $"storage-{Id}-client";
    private string ClientIdName => $"storage-{Id}-clientid";

    public IReadOnlyList<StorageSetting> Settings
    {
        get
        {
            List<StorageSetting> settings = [new("clientid", "storage.cloud.clientId", ClientId, IsSecret: true, Note: SourceNote(ClientId, EnvClientIdVar))];
            if (UsesClientSecret)
            {
                var secret = Secrets.Load(ClientSecretName) ?? "";
                settings.Add(new("clientsecret", "storage.cloud.clientSecret", secret, IsSecret: true, Masked: true, Note: SourceNote(secret, EnvClientSecretVar)));
            }

            settings.AddRange(Cache.Settings);
            return settings;
        }
    }

    // Skąd pochodzi wartość pola: zapisana w sekretach, ze zmiennej środowiska (.env) albo brak
    private StorageStatus SourceNote(string? saved, string? envVar) =>
        !string.IsNullOrWhiteSpace(saved) ? new("storage.note.saved")
        : envVar is not null && !string.IsNullOrEmpty(_env(envVar)) ? new("storage.note.env", envVar)
        : new("storage.note.missing");

    // Puste pole nie kasuje zapisanej wartości (okno mogło być otwarte ze starymi, pustymi danymi); nowa wartość ją zastępuje
    public void Configure(IReadOnlyDictionary<string, string> values)
    {
        if (values.TryGetValue("clientid", out var id) && !string.IsNullOrWhiteSpace(id)) Secrets.Save(id.Trim(), ClientIdName);
        if (UsesClientSecret && values.TryGetValue("clientsecret", out var secret) && !string.IsNullOrWhiteSpace(secret))
            Secrets.Save(secret.Trim(), ClientSecretName);
        Cache.Configure(values);
    }

    public string? LocalFolder(MediaKind kind) => Cache.LocalFolder(kind);

    public IReadOnlyList<StorageAction> Actions => [new("signin", "storage.cloud.signIn"), new("signout", "storage.cloud.signOut")];

    private bool SignedIn => Secrets.Load(TokenName) is not null;

    public bool CanRun(string key) => key switch { "signin" => !SignedIn, "signout" => SignedIn, _ => true };

    public StorageStatus? Status => _lastError is not null ? new("storage.status.error", _lastError)
        : !SignedIn ? new("storage.status.signedOut")
        : TokenEmail() is { } email ? new("storage.status.signedInAs", email) : new("storage.status.signedIn");

    private string? TokenEmail()
    {
        try { return Secrets.Load(TokenName) is { } json ? JsonSerializer.Deserialize<OAuthTokens>(json)?.Email : null; }
        catch (JsonException) { return null; }
    }

    public async Task RunActionAsync(string key, CancellationToken ct = default)
    {
        _lastError = null;
        if (key == "signout")
        {
            Secrets.Save("", TokenName);
            return;
        }

        if (key != "signin") return;
        if (string.IsNullOrWhiteSpace(EffectiveClientId)) throw new InvalidOperationException("Podaj Client ID aplikacji (zobacz opis poniżej).");
        var tokens = await Flow.SignInAsync(Endpoints, EffectiveClientId, EffectiveClientSecret, ct);
        Secrets.Save(JsonSerializer.Serialize(tokens), TokenName);
    }

    // Token dostępu z odświeżeniem; null = niezalogowany
    protected async Task<string?> AccessTokenAsync(CancellationToken ct)
    {
        if (Secrets.Load(TokenName) is not { } json) return null;
        var tokens = JsonSerializer.Deserialize<OAuthTokens>(json)!;
        if (tokens.IsFresh) return tokens.AccessToken;

        if (tokens.RefreshToken is null) throw new InvalidOperationException("Sesja wygasła: zaloguj się ponownie w Ustawieniach (Zapis).");
        tokens = await Flow.RefreshAsync(Endpoints, EffectiveClientId, EffectiveClientSecret, tokens.RefreshToken, ct, tokens.Email);
        Secrets.Save(JsonSerializer.Serialize(tokens), TokenName);
        return tokens.AccessToken;
    }

    public async Task<string> WriteAsync(MediaKind kind, string fileName, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var path = await Cache.WriteAsync(kind, fileName, data, ct);
        var bytes = data.ToArray();
        Background(async () =>
        {
            if (await AccessTokenAsync(CancellationToken.None) is { } token)
                await UploadAsync(token, Path.GetFileName(path), bytes, CancellationToken.None);
        });
        return path;
    }

    public async Task DeleteAsync(string location, CancellationToken ct = default)
    {
        await Cache.DeleteAsync(location, ct);
        Background(async () =>
        {
            if (await AccessTokenAsync(CancellationToken.None) is { } token)
                await DeleteRemoteAsync(token, Path.GetFileName(location), CancellationToken.None);
        });
    }

    // Czeka na zakończenie wysyłek w tle (testy i zamykanie aplikacji)
    public Task FlushAsync() => Task.WhenAll(_pending);

    private void Background(Func<Task> work) => _pending.Add(Task.Run(async () =>
    {
        try
        {
            await work();
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }
    }));

    public Task<byte[]> ReadAsync(string location, CancellationToken ct = default) => Cache.ReadAsync(location, ct);
    public Task<bool> ExistsAsync(string location, CancellationToken ct = default) => Cache.ExistsAsync(location, ct);
    public Task<string> GetLocalPathAsync(string location, CancellationToken ct = default) => Cache.GetLocalPathAsync(location, ct);

    protected static async Task EnsureOkAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        throw new HttpRequestException($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
    }
}
