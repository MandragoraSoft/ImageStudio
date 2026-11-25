using System.Net.Http;
using System.Net.Http.Headers;
using ImageStudio.Storage.Cloud;

namespace ImageStudio.Storage.OneDrive;

// OneDrive (konto osobiste lub służbowe) przez Microsoft Graph. Zakres Files.ReadWrite.AppFolder: aplikacja widzi tylko
// własny folder Aplikacje\<nazwa rejestracji> (nie cały dysk). Rejestracja w Azure: aplikacja publiczna ("Mobile and
// desktop"), adres przekierowania http://localhost, uprawnienie delegowane Files.ReadWrite.AppFolder
public sealed class OneDriveStorage : OAuthCloudStorage
{
    public OneDriveStorage(IStorageSecrets secrets, HttpMessageHandler? handler = null, Func<string, string?>? environment = null)
        : base(secrets, handler, environment) { }

    public OneDriveStorage(IStorageSecrets secrets, HttpMessageHandler handler, TimeSpan timeout)
        : base(secrets, handler, null, timeout) { }
    public const string GraphRoot = "https://graph.microsoft.com/v1.0/me/drive/special/approot:/";

    public override string Id => "onedrive";
    public override string DisplayName => "OneDrive";
    public override string? HelpKey => "storage.onedrive.help";
    protected override string EnvClientIdVar => "AZURE_APP_CLIENT_ID";

    protected override OAuthEndpoints Endpoints { get; } = new(
        "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
        "https://login.microsoftonline.com/common/oauth2/v2.0/token",
        "Files.ReadWrite.AppFolder offline_access openid email", RedirectHost: "localhost");

    private static string Url(string fileName, string suffix) => GraphRoot + Uri.EscapeDataString(fileName) + suffix;

    // ponytail: prosty PUT działa do 250 MB; większe wideo wymagałoby sesji wysyłania (createUploadSession)
    protected override async Task UploadAsync(string accessToken, string fileName, byte[] data, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Url(fileName, ":/content")) { Content = new ByteArrayContent(data) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await Http.SendAsync(request, ct);
        await EnsureOkAsync(response, ct);
    }

    protected override async Task<byte[]?> DownloadRemoteAsync(string accessToken, string fileName, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(fileName, ":/content"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await EnsureOkAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    protected override async Task DeleteRemoteAsync(string accessToken, string fileName, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Url(fileName, ""));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound) await EnsureOkAsync(response, ct);
    }
}
