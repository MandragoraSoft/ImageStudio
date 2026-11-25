using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ImageStudio.Storage.Cloud;

namespace ImageStudio.Storage.GoogleDrive;

// Google Drive przez API v3. Zakres drive.file: aplikacja widzi tylko pliki, które sama utworzyła (folder ImageStudio
// w Moim dysku). Rejestracja w Google Cloud: klient OAuth typu "Aplikacja komputerowa" (Client ID i sekret), włączone
// Google Drive API
public sealed class GoogleDriveStorage : OAuthCloudStorage
{
    public GoogleDriveStorage(IStorageSecrets secrets, HttpMessageHandler? handler = null, Func<string, string?>? environment = null)
        : base(secrets, handler, environment) { }

    public GoogleDriveStorage(IStorageSecrets secrets, HttpMessageHandler handler, TimeSpan timeout)
        : base(secrets, handler, null, timeout) { }
    public const string FolderName = "ImageStudio";
    private const string Api = "https://www.googleapis.com/drive/v3/files";

    private readonly SemaphoreSlim _folderLock = new(1, 1);
    private string? _folderId;

    public override string Id => "googledrive";
    public override string DisplayName => "Google Drive";
    public override string? HelpKey => "storage.googledrive.help";
    protected override bool UsesClientSecret => true;
    protected override string EnvClientIdVar => "GOOGLE_OAUTH_CLIENT_ID";
    protected override string? EnvClientSecretVar => "GOOGLE_OAUTH_CLIENT_SECRET";

    protected override OAuthEndpoints Endpoints { get; } = new(
        "https://accounts.google.com/o/oauth2/v2/auth", "https://oauth2.googleapis.com/token",
        "https://www.googleapis.com/auth/drive.file openid email", RedirectHost: "127.0.0.1",
        AuthorizeExtra: new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent" });

    private static HttpRequestMessage Request(HttpMethod method, string url, string token, HttpContent? content = null) =>
        new(method, url) { Content = content, Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } };

    private async Task<JsonElement> JsonAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await Http.SendAsync(request, ct);
        await EnsureOkAsync(response, ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();
    }

    private static string FirstId(JsonElement list) =>
        list.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("id").GetString()!).FirstOrDefault() ?? "";

    // Folder szukany po nazwie (zakres drive.file widzi tylko własne), a gdy go nie ma, tworzony
    private async Task<string> FolderIdAsync(string token, CancellationToken ct)
    {
        if (_folderId is not null) return _folderId;

        // Wysyłki idą w tle równolegle: bez blokady każda utworzyłaby własny folder
        await _folderLock.WaitAsync(ct);
        try
        {
            return _folderId ??= await FindOrCreateFolderAsync(token, ct);
        }
        finally
        {
            _folderLock.Release();
        }
    }

    private async Task<string> FindOrCreateFolderAsync(string token, CancellationToken ct)
    {

        var q = Uri.EscapeDataString($"name='{FolderName}' and mimeType='application/vnd.google-apps.folder' and trashed=false");
        var id = FirstId(await JsonAsync(Request(HttpMethod.Get, $"{Api}?q={q}&fields=files(id)", token), ct));
        if (id == "")
        {
            var body = JsonSerializer.Serialize(new { name = FolderName, mimeType = "application/vnd.google-apps.folder" });
            var created = await JsonAsync(Request(HttpMethod.Post, Api, token, new StringContent(body, Encoding.UTF8, "application/json")), ct);
            id = created.GetProperty("id").GetString()!;
        }

        return id;
    }

    // Id pliku o tej nazwie w folderze aplikacji; "" = brak
    private async Task<string> FileIdAsync(string token, string folder, string fileName, CancellationToken ct)
    {
        var q = Uri.EscapeDataString($"name='{fileName.Replace("'", "\\'")}' and '{folder}' in parents and trashed=false");
        return FirstId(await JsonAsync(Request(HttpMethod.Get, $"{Api}?q={q}&fields=files(id)", token), ct));
    }

    // Drive pozwala na wiele plików o tej samej nazwie, więc przy ponownym zapisie aktualizujemy istniejący (bez duplikatów)
    protected override async Task UploadAsync(string accessToken, string fileName, byte[] data, CancellationToken ct)
    {
        var folder = await FolderIdAsync(accessToken, ct);
        var existing = await FileIdAsync(accessToken, folder, fileName, ct);
        if (existing != "")
        {
            await JsonAsync(Request(new HttpMethod("PATCH"), $"https://www.googleapis.com/upload/drive/v3/files/{existing}?uploadType=media",
                                    accessToken, new ByteArrayContent(data)), ct);
            return;
        }

        var metadata = JsonSerializer.Serialize(new { name = fileName, parents = new[] { folder } });
        var content = new MultipartContent("related")
        {
            new StringContent(metadata, Encoding.UTF8, "application/json"),
            new ByteArrayContent(data),
        };
        await JsonAsync(Request(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart", accessToken, content), ct);
    }

    protected override async Task<byte[]?> DownloadRemoteAsync(string accessToken, string fileName, CancellationToken ct)
    {
        var id = await FileIdAsync(accessToken, await FolderIdAsync(accessToken, ct), fileName, ct);
        if (id == "") return null;

        using var response = await Http.SendAsync(Request(HttpMethod.Get, $"{Api}/{id}?alt=media", accessToken), ct);
        await EnsureOkAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    protected override async Task DeleteRemoteAsync(string accessToken, string fileName, CancellationToken ct)
    {
        var id = await FileIdAsync(accessToken, await FolderIdAsync(accessToken, ct), fileName, ct);
        if (id == "") return;

        using var response = await Http.SendAsync(Request(HttpMethod.Delete, $"{Api}/{id}", accessToken), ct);
        await EnsureOkAsync(response, ct);
    }
}
