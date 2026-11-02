using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.Storage;
using ImageStudio.Storage.Cloud;
using ImageStudio.Storage.GoogleDrive;
using ImageStudio.Storage.OneDrive;

namespace ImageStudio.Tests;

public sealed class CloudStorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "is-cloud-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class MemorySecrets : IStorageSecrets
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? Load(string name) => Values.GetValueOrDefault(name);

        public void Save(string value, string name)
        {
            if (value == "") Values.Remove(name);
            else Values[name] = value;
        }
    }

    private sealed record Call(HttpMethod Method, string Url, string? Auth, string Body);

    private sealed class StubHandler(Func<Call, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : Encoding.UTF8.GetString(await request.Content.ReadAsByteArrayAsync(ct));
            var call = new Call(request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body);
            lock (Calls) Calls.Add(call);
            var (status, json) = respond(call);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static string Tokens(string access, string? refresh, int expiresInSeconds) =>
        JsonSerializer.Serialize(new OAuthTokens(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds)));

    private T Mirror<T>(T plugin) where T : CloudMirrorStorage
    {
        foreach (var kind in Enum.GetValues<MediaKind>()) plugin.Cache.Roots[kind] = Path.Combine(_dir, kind.ToString());
        return plugin;
    }

    [Fact]
    public async Task OneDrive_saves_locally_and_uploads_to_the_app_folder_with_the_bearer_token()
    {
        var secrets = new MemorySecrets { Values = { ["storage-onedrive"] = Tokens("tok", "ref", 3600) } };
        var http = new StubHandler(_ => (HttpStatusCode.Created, "{}"));
        var storage = Mirror(new OneDriveStorage(secrets, http));

        var path = await storage.WriteAsync(MediaKind.Image, "a b.png", new byte[] { 1, 2, 3 });
        await storage.FlushAsync();

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        var call = Assert.Single(http.Calls);
        Assert.Equal(HttpMethod.Put, call.Method);
        Assert.Equal(OneDriveStorage.GraphRoot + "a%20b.png:/content", call.Url);
        Assert.Equal("Bearer tok", call.Auth);
        Assert.Equal(new byte[] { 1, 2, 3 }, Encoding.UTF8.GetBytes(call.Body)); // bajty < 0x80 przechodzą bez zmian
    }

    [Fact]
    public async Task Signed_out_storage_only_saves_locally()
    {
        var http = new StubHandler(_ => (HttpStatusCode.OK, "{}"));
        var storage = Mirror(new OneDriveStorage(new MemorySecrets(), http));

        var path = await storage.WriteAsync(MediaKind.Video, "v.mp4", new byte[] { 9 });
        await storage.FlushAsync();

        Assert.True(File.Exists(path));
        Assert.Empty(http.Calls);
        Assert.Equal("storage.status.signedOut", storage.Status!.Key);
    }

    [Fact]
    public async Task Expired_token_is_refreshed_before_the_upload_and_saved()
    {
        var secrets = new MemorySecrets { Values = { ["storage-onedrive"] = Tokens("old", "ref", -10) } };
        var http = new StubHandler(c => c.Url.Contains("/token")
            ? (HttpStatusCode.OK, """{"access_token":"new","expires_in":3600}""")
            : (HttpStatusCode.OK, "{}"));
        var storage = Mirror(new OneDriveStorage(secrets, http));
        storage.Configure(new Dictionary<string, string> { ["clientid"] = "cid" });

        await storage.WriteAsync(MediaKind.Image, "a.png", new byte[] { 1 });
        await storage.FlushAsync();

        Assert.Contains("grant_type=refresh_token", http.Calls[0].Body);
        Assert.Contains("refresh_token=ref", http.Calls[0].Body);
        Assert.Equal("Bearer new", http.Calls[1].Auth);
        var saved = JsonSerializer.Deserialize<OAuthTokens>(secrets.Values["storage-onedrive"])!;
        Assert.Equal(("new", "ref"), (saved.AccessToken, saved.RefreshToken)); // stary refresh token zostaje
    }

    [Fact]
    public async Task Failed_upload_does_not_fail_the_save_and_is_reported_in_the_status()
    {
        var secrets = new MemorySecrets { Values = { ["storage-onedrive"] = Tokens("tok", null, 3600) } };
        var storage = Mirror(new OneDriveStorage(secrets, new StubHandler(_ => (HttpStatusCode.Forbidden, "brak miejsca"))));

        var path = await storage.WriteAsync(MediaKind.Image, "a.png", new byte[] { 1 });
        await storage.FlushAsync();

        Assert.True(File.Exists(path));
        Assert.Equal("storage.status.error", storage.Status!.Key);
        Assert.Contains("brak miejsca", storage.Status.Detail);
    }

    [Fact]
    public async Task GoogleDrive_creates_the_folder_once_and_uploads_into_it()
    {
        var secrets = new MemorySecrets { Values = { ["storage-googledrive"] = Tokens("tok", "ref", 3600) } };
        var http = new StubHandler(c => c.Url.Contains("?q=") ? (HttpStatusCode.OK, """{"files":[]}""")
            : c.Body.Contains("folder") ? (HttpStatusCode.OK, """{"id":"F1"}""") : (HttpStatusCode.OK, """{"id":"X"}"""));
        var storage = Mirror(new GoogleDriveStorage(secrets, http));

        await storage.WriteAsync(MediaKind.Image, "a.png", new byte[] { 1 });
        await storage.FlushAsync();
        await storage.WriteAsync(MediaKind.Image, "b.png", new byte[] { 2 });
        await storage.FlushAsync();

        Assert.Single(http.Calls, c => c.Url.Contains("?q=") && c.Url.Contains("mimeType")); // folder szukany raz
        Assert.Single(http.Calls, c => c.Method == HttpMethod.Post && c.Body.Contains("\"mimeType\":\"application/vnd.google-apps.folder\""));
        var uploads = http.Calls.Where(c => c.Url.Contains("uploadType=multipart")).ToList();
        Assert.Equal(2, uploads.Count);
        Assert.All(uploads, u => Assert.Contains("\"parents\":[\"F1\"]", u.Body));
    }

    [Fact]
    public async Task Sign_in_goes_through_the_browser_loopback_and_stores_the_tokens()
    {
        var secrets = new MemorySecrets();
        var http = new StubHandler(_ => (HttpStatusCode.OK, """{"access_token":"tok","refresh_token":"ref","expires_in":3600}"""));
        var storage = Mirror(new OneDriveStorage(secrets, http));
        storage.Configure(new Dictionary<string, string> { ["clientid"] = "cid" });
        string? authorizeUrl = null;
        storage.BrowserOverride = url =>
        {
            authorizeUrl = url;
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            // "przeglądarka" wraca na adres przekierowania z kodem i tym samym state
            _ = Task.Run(async () => await new HttpClient().GetAsync($"{query["redirect_uri"]}?code=CODE&state={query["state"]}"));
        };

        await storage.RunActionAsync("signin");

        Assert.Contains("login.microsoftonline.com", authorizeUrl);
        Assert.Contains("code_challenge_method=S256", authorizeUrl);
        Assert.Contains("client_id=cid", authorizeUrl);
        Assert.Contains("code=CODE", http.Calls.Single().Body);
        Assert.Contains("code_verifier=", http.Calls.Single().Body);
        Assert.Equal("tok", JsonSerializer.Deserialize<OAuthTokens>(secrets.Values["storage-onedrive"])!.AccessToken);
        Assert.Equal("storage.status.signedIn", storage.Status!.Key);

        await storage.RunActionAsync("signout");
        Assert.Equal("storage.status.signedOut", storage.Status!.Key);
    }

    [Fact]
    public async Task Sign_in_without_client_id_explains_what_is_missing()
    {
        var storage = Mirror(new OneDriveStorage(new MemorySecrets(), new StubHandler(_ => (HttpStatusCode.OK, "{}")), _ => null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.RunActionAsync("signin"));
    }

    [Fact]
    public void Client_secret_goes_to_the_secrets_and_never_to_the_persisted_config()
    {
        var secrets = new MemorySecrets();
        var google = Mirror(new GoogleDriveStorage(secrets));
        var host = new MediaStorageHost([google]);
        var config = new Dictionary<string, Dictionary<string, string>>
        {
            ["googledrive"] = new() { ["clientid"] = "cid", ["clientsecret"] = "sekret", ["image"] = "C:\\x" },
        };

        host.Apply("googledrive", config);

        Assert.Equal("sekret", secrets.Values["storage-googledrive-client"]);
        var persisted = host.WithoutSecrets(config)["googledrive"];
        Assert.False(persisted.ContainsKey("clientsecret"));
        Assert.False(persisted.ContainsKey("clientid"));
        Assert.Equal("cid", secrets.Values["storage-googledrive-clientid"]);
        Assert.Equal("C:\\x", persisted["image"]);
    }

    [Fact]
    public async Task Empty_settings_fall_back_to_client_id_and_secret_from_the_environment()
    {
        var env = new Dictionary<string, string> { ["GOOGLE_OAUTH_CLIENT_ID"] = "env-cid", ["GOOGLE_OAUTH_CLIENT_SECRET"] = "env-sekret" };
        var http = new StubHandler(_ => (HttpStatusCode.OK, """{"access_token":"tok","refresh_token":"ref","expires_in":3600}"""));
        var storage = Mirror(new GoogleDriveStorage(new MemorySecrets(), http, name => env.GetValueOrDefault(name)));
        string? authorizeUrl = null;
        storage.BrowserOverride = url =>
        {
            authorizeUrl = url;
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            _ = Task.Run(async () => await new HttpClient().GetAsync($"{query["redirect_uri"]}?code=C&state={query["state"]}"));
        };

        await storage.RunActionAsync("signin");

        Assert.Contains("client_id=env-cid", authorizeUrl);
        Assert.Contains("client_secret=env-sekret", http.Calls.Single().Body);
        Assert.Equal("", storage.Settings.Single(x => x.Key == "clientid").Value); // pole zostaje puste, nic nie trafia do sekretów
    }

    [Fact]
    public void Dotenv_parser_reads_pairs_comments_quotes_and_export()
    {
        var pairs = DotEnv.Parse(["# komentarz", "", "A=1", "  B = dwa słowa ", "C=\"w cudzysłowie\"", "export D='x'", "brak-znaku", "=pusty", "E="]).ToList();

        Assert.Equal([("A", "1"), ("B", "dwa słowa"), ("C", "w cudzysłowie"), ("D", "x"), ("E", "")], pairs);
    }

    [Fact]
    public void Empty_field_does_not_erase_a_saved_client_id_and_the_note_tells_where_the_value_comes_from()
    {
        var secrets = new MemorySecrets { Values = { ["storage-onedrive-clientid"] = "zapisany" } };
        var storage = Mirror(new OneDriveStorage(secrets, environment: name => name == "AZURE_APP_CLIENT_ID" ? "z-env" : null));

        storage.Configure(new Dictionary<string, string> { ["clientid"] = "  " });

        Assert.Equal("zapisany", secrets.Values["storage-onedrive-clientid"]);
        Assert.Equal("storage.note.saved", storage.Settings.Single(x => x.Key == "clientid").Note!.Key);

        secrets.Values.Clear();
        var note = storage.Settings.Single(x => x.Key == "clientid").Note!;
        Assert.Equal(("storage.note.env", "AZURE_APP_CLIENT_ID"), (note.Key, note.Detail));
        Assert.Equal("storage.note.missing", Mirror(new OneDriveStorage(secrets, environment: _ => null)).Settings.First().Note!.Key);
    }

    [Fact]
    public void Sign_in_is_available_only_when_signed_out_and_sign_out_only_when_signed_in()
    {
        var secrets = new MemorySecrets();
        var storage = Mirror(new OneDriveStorage(secrets, environment: _ => null));
        Assert.True(storage.CanRun("signin"));
        Assert.False(storage.CanRun("signout"));

        secrets.Values["storage-onedrive"] = Tokens("tok", "ref", 3600);

        Assert.False(storage.CanRun("signin"));
        Assert.True(storage.CanRun("signout"));
    }

    [Fact]
    public void Signed_in_status_shows_the_account_email_from_the_id_token()
    {
        string Jwt(string json) => "h." + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".s";
        Assert.Equal("a@b.pl", OAuthFlow.EmailFrom(Jwt("""{"email":"a@b.pl"}""")));
        Assert.Equal("login@live.com", OAuthFlow.EmailFrom(Jwt("""{"preferred_username":"login@live.com"}""")));
        Assert.Null(OAuthFlow.EmailFrom("nie-jwt"));

        var secrets = new MemorySecrets { Values = { ["storage-onedrive"] = JsonSerializer.Serialize(
            new OAuthTokens("tok", "ref", DateTimeOffset.UtcNow.AddHours(1), "a@b.pl")) } };
        var status = Mirror(new OneDriveStorage(secrets, environment: _ => null)).Status!;
        Assert.Equal(("storage.status.signedInAs", "a@b.pl"), (status.Key, status.Detail));
    }

    [Fact]
    public async Task GoogleDrive_updates_an_existing_file_instead_of_creating_a_duplicate()
    {
        var secrets = new MemorySecrets { Values = { ["storage-googledrive"] = Tokens("tok", "ref", 3600) } };
        var http = new StubHandler(c => c.Url.Contains("mimeType") ? (HttpStatusCode.OK, """{"files":[{"id":"F1"}]}""")
            : c.Url.Contains("?q=") ? (HttpStatusCode.OK, """{"files":[{"id":"FILE9"}]}""") : (HttpStatusCode.OK, """{"id":"FILE9"}"""));
        var storage = Mirror(new GoogleDriveStorage(secrets, http));

        await storage.WriteAsync(MediaKind.Image, "a.png", new byte[] { 7 });
        await storage.FlushAsync();

        var update = Assert.Single(http.Calls, c => c.Method.Method == "PATCH");
        Assert.Contains("/files/FILE9?uploadType=media", update.Url);
        Assert.DoesNotContain(http.Calls, c => c.Url.Contains("uploadType=multipart"));
    }

    [Fact]
    public async Task Missing_local_file_is_read_back_from_the_cloud_and_cached_at_the_same_path()
    {
        var secrets = new MemorySecrets { Values = { ["storage-onedrive"] = Tokens("tok", "ref", 3600) } };
        var http = new StubHandler(c => c.Method == HttpMethod.Get ? (HttpStatusCode.OK, "z chmury") : (HttpStatusCode.OK, "{}"));
        var storage = Mirror(new OneDriveStorage(secrets, http));
        var path = Path.Combine(_dir, "Image", "gone.png");

        Assert.Equal("z chmury", Encoding.UTF8.GetString(await storage.ReadAsync(path)));
        Assert.Equal(path, await storage.GetLocalPathAsync(path));
        Assert.True(File.Exists(path));
        Assert.Single(http.Calls, c => c.Method == HttpMethod.Get); // drugi odczyt jest już lokalny
    }

    [Fact]
    public async Task Missing_file_everywhere_is_reported()
    {
        var secrets = new MemorySecrets { Values = { ["storage-onedrive"] = Tokens("tok", "ref", 3600) } };
        var storage = Mirror(new OneDriveStorage(secrets, new StubHandler(_ => (HttpStatusCode.NotFound, "{}"))));

        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.ReadAsync(Path.Combine(_dir, "Image", "nie-ma.png")));
    }

    [Fact]
    public async Task GoogleDrive_parallel_uploads_share_one_folder()
    {
        var secrets = new MemorySecrets { Values = { ["storage-googledrive"] = Tokens("tok", "ref", 3600) } };
        var http = new StubHandler(c =>
        {
            Thread.Sleep(30); // wyłączenie szczęśliwego trafu: równoległe wysyłki zdążą się nałożyć
            return c.Url.Contains("?q=") ? (HttpStatusCode.OK, """{"files":[]}""")
                : c.Body.Contains("google-apps.folder") ? (HttpStatusCode.OK, """{"id":"F1"}""") : (HttpStatusCode.OK, """{"id":"X"}""");
        });
        var storage = Mirror(new GoogleDriveStorage(secrets, http));

        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => storage.WriteAsync(MediaKind.Image, $"{i}.png", new byte[] { 1 })));
        await storage.FlushAsync();

        Assert.Single(http.Calls, c => c.Method == HttpMethod.Post && c.Body.Contains("google-apps.folder"));
    }
}
