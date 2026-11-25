using System.Net.Http;
using System.Text.Json;

namespace ImageStudio.Storage.Cloud;

// Wtyczki chmurowe logowane przez OAuth w przeglądarce (OneDrive, Google Drive): Client ID (i sekret), tokeny w sekrecie
// storage-{Id}, odświeżanie tokenu, klient HTTP z handlerem aplikacji (rejestr żądań)
public abstract class OAuthCloudStorage : CloudMirrorStorage
{
    private readonly Func<string, string?> _env;

    protected OAuthCloudStorage(IStorageSecrets secrets, HttpMessageHandler? handler = null, Func<string, string?>? environment = null)
        : this(secrets, handler, environment, null) { }

    // environment: odczyt zmiennych środowiska (testy podstawiają własny); domyślnie Environment
    protected OAuthCloudStorage(IStorageSecrets secrets, HttpMessageHandler? handler, Func<string, string?>? environment, TimeSpan? timeout)
        : base(secrets)
    {
        _env = environment ?? Environment.GetEnvironmentVariable;
        Http = new HttpClient(handler ?? new HttpClientHandler());
        if (timeout is { } value) Http.Timeout = value;
        else if (handler is null) Http.Timeout = TimeSpan.FromMinutes(10);
        Flow = new OAuthFlow(Http, OpenBrowser);
    }

    protected abstract OAuthEndpoints Endpoints { get; }

    // Google wymaga sekretu klienta także od aplikacji desktopowej; Microsoft nie
    protected virtual bool UsesClientSecret => false;

    // Zmienne środowiska (np. z pliku .env) z domyślnym Client ID i sekretem; używane, gdy w ustawieniach pole jest puste
    protected abstract string EnvClientIdVar { get; }
    protected virtual string? EnvClientSecretVar => null;

    protected HttpClient Http { get; }
    public OAuthFlow Flow { get; }

    private void OpenBrowser(string url) => (BrowserOverride ?? (u => System.Diagnostics.Process.Start(
        new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true })))(url);

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

    protected override bool SignedIn => Secrets.Load(TokenName) is not null;

    protected override string? AccountLabel => TokenEmail();

    protected override IEnumerable<StorageSetting> AuthSettings
    {
        get
        {
            List<StorageSetting> settings = [new("clientid", "storage.cloud.clientId", ClientId, IsSecret: true, Note: SourceNote(ClientId, EnvClientIdVar))];
            if (UsesClientSecret)
            {
                var secret = Secrets.Load(ClientSecretName) ?? "";
                settings.Add(new("clientsecret", "storage.cloud.clientSecret", secret, IsSecret: true, Masked: true, Note: SourceNote(secret, EnvClientSecretVar)));
            }

            return settings;
        }
    }

    // Skąd pochodzi wartość pola: zapisana w sekretach, ze zmiennej środowiska (.env) albo brak
    private StorageStatus SourceNote(string? saved, string? envVar) =>
        !string.IsNullOrWhiteSpace(saved) ? new("storage.note.saved")
        : envVar is not null && !string.IsNullOrEmpty(_env(envVar)) ? new("storage.note.env", envVar)
        : new("storage.note.missing");

    // Puste pole nie kasuje zapisanej wartości (okno mogło być otwarte ze starymi, pustymi danymi); nowa wartość ją zastępuje
    protected override void ConfigureAuth(IReadOnlyDictionary<string, string> values)
    {
        if (values.TryGetValue("clientid", out var id) && !string.IsNullOrWhiteSpace(id)) Secrets.Save(id.Trim(), ClientIdName);
        if (UsesClientSecret && values.TryGetValue("clientsecret", out var secret) && !string.IsNullOrWhiteSpace(secret))
            Secrets.Save(secret.Trim(), ClientSecretName);
    }

    private string? TokenEmail()
    {
        try { return Secrets.Load(TokenName) is { } json ? JsonSerializer.Deserialize<OAuthTokens>(json)?.Email : null; }
        catch (JsonException) { return null; }
    }

    protected override async Task SignInAsync(IReadOnlyDictionary<string, string> inputs, IStorageInteraction interaction, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(EffectiveClientId)) throw new InvalidOperationException("Podaj Client ID aplikacji (zobacz opis poniżej).");
        var tokens = await Flow.SignInAsync(Endpoints, EffectiveClientId, EffectiveClientSecret, ct);
        Secrets.Save(JsonSerializer.Serialize(tokens), TokenName);
    }

    protected override Task SignOutAsync(CancellationToken ct)
    {
        Secrets.Save("", TokenName);
        return Task.CompletedTask;
    }

    // Token dostępu z odświeżeniem; null = niezalogowany
    protected override async Task<string?> AccessTokenAsync(CancellationToken ct)
    {
        if (Secrets.Load(TokenName) is not { } json) return null;
        var tokens = JsonSerializer.Deserialize<OAuthTokens>(json)!;
        if (tokens.IsFresh) return tokens.AccessToken;

        if (tokens.RefreshToken is null) throw new InvalidOperationException("Sesja wygasła: zaloguj się ponownie w Ustawieniach (Zapis).");
        tokens = await Flow.RefreshAsync(Endpoints, EffectiveClientId, EffectiveClientSecret, tokens.RefreshToken, ct, tokens.Email);
        Secrets.Save(JsonSerializer.Serialize(tokens), TokenName);
        return tokens.AccessToken;
    }

    protected static async Task EnsureOkAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        throw new HttpRequestException($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
    }
}
