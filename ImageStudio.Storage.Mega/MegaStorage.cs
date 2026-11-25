using System.Text.Json;
using ImageStudio.Localization;
using ImageStudio.Storage.Cloud;

namespace ImageStudio.Storage.Mega;

// MEGA (nieoficjalna biblioteka MegaApiClient) w modelu lustra: pliki w folderze ImageStudio na koncie. Logowanie e-mailem
// i hasłem w zakładce Zapis (opcjonalnie kod 2FA); w sekrecie storage-mega zostaje tylko e-mail i sesja, nigdy hasło
public sealed class MegaStorage : CloudMirrorStorage
{
    public const string FolderName = "ImageStudio";
    private readonly IMegaClient _client;

    public MegaStorage(StoragePluginContext context)
        : this(context.Secrets, new MegaApiClientAdapter(context.CreateHttpHandler(), context.HttpTimeout)) { }

    public MegaStorage(IStorageSecrets secrets, IMegaClient client) : base(secrets) => _client = client;

    public override string Id => "mega";
    public override string DisplayName => "MEGA";
    public override string? HelpKey => "storage.mega.help";
    protected override string SignInLabelKey => "storage.mega.signIn";
    protected override string? SignInWorkingKey => "storage.mega.working";

    private sealed record MegaSession(string Email, string Session);
    private string SessionName => $"storage-{Id}";

    private MegaSession? LoadSession()
    {
        try { return Secrets.Load(SessionName) is { } json ? JsonSerializer.Deserialize<MegaSession>(json) : null; }
        catch (JsonException) { return null; }
    }

    protected override bool SignedIn => LoadSession() is not null;
    protected override string? AccountLabel => LoadSession()?.Email;
    protected override Task<string?> AccessTokenAsync(CancellationToken ct) => Task.FromResult(LoadSession()?.Session);

    protected override IEnumerable<StorageSetting> AuthSettings =>
    [
        new("email", "storage.mega.email", "", Kind: StorageFieldKind.Email, Transient: true),
        new("password", "storage.mega.password", "", Masked: true, Kind: StorageFieldKind.Password, Transient: true),
    ];

    protected override async Task SignInAsync(IReadOnlyDictionary<string, string> inputs, IStorageInteraction interaction, CancellationToken ct)
    {
        var email = inputs.GetValueOrDefault("email")?.Trim() ?? "";
        var password = inputs.GetValueOrDefault("password") ?? "";
        if (email.Length == 0 || password.Length == 0) throw new InvalidOperationException(Loc.T("storage.mega.missingCredentials"));

        string session;
        try
        {
            session = await _client.LoginAsync(email, password, null, ct);
        }
        catch (MegaMfaRequiredException)
        {
            var code = await interaction.AskAsync(new StoragePrompt("mfa", "storage.mega.mfaCode", StorageFieldKind.Code,
                                                                    new StorageStatus("storage.mega.mfaPrompt")), ct);
            if (string.IsNullOrWhiteSpace(code)) throw new OperationCanceledException(ct);
            session = await _client.LoginAsync(email, password, code.Trim(), ct);
        }

        Secrets.Save(JsonSerializer.Serialize(new MegaSession(email, session)), SessionName);
    }

    // Najpierw lokalnie: błąd sieci przy unieważnianiu nie może zostawić użytkownika zalogowanym
    protected override async Task SignOutAsync(CancellationToken ct)
    {
        var session = LoadSession();
        Secrets.Save("", SessionName);
        if (session is null) return;
        try { await _client.LogoutAsync(session.Session, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { } // sesja i tak zapomniana lokalnie
    }

    protected override Task UploadAsync(string accessToken, string fileName, byte[] data, CancellationToken ct) =>
        _client.UploadAsync(accessToken, FolderName, fileName, data, ct);

    protected override Task<byte[]?> DownloadRemoteAsync(string accessToken, string fileName, CancellationToken ct) =>
        _client.DownloadAsync(accessToken, FolderName, fileName, ct);

    protected override Task DeleteRemoteAsync(string accessToken, string fileName, CancellationToken ct) =>
        _client.DeleteAsync(accessToken, FolderName, fileName, ct);
}
