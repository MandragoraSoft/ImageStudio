using System.Text.Json;
using ImageStudio.Composition;
using ImageStudio.Localization;
using ImageStudio.Services;
using ImageStudio.Storage;
using ImageStudio.Storage.Mega;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

public sealed class MegaStorageTests : IDisposable
{
    private const string Email = "ania@example.com";
    private const string Password = "tajne-haslo-123";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "is-mega-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // Atrapa klienta MEGA: pliki w pamięci, sesja to "sesja-<e-mail>", logowanie 2FA wymaga kodu FakeMegaClient.Code
    private sealed class FakeMegaClient : IMegaClient
    {
        public const string Code = "654321";

        public Dictionary<(string Folder, string Name), byte[]> Files { get; } = [];
        public List<string> Calls { get; } = [];
        public bool TwoFactor { get; set; }
        public bool LogoutFails { get; set; }

        public Task<string> LoginAsync(string email, string password, string? mfaCode, CancellationToken ct)
        {
            Calls.Add("login");
            if (password != Password) throw new InvalidOperationException("Błędne hasło.");
            if (TwoFactor && mfaCode is null) throw new MegaMfaRequiredException();
            if (TwoFactor && mfaCode != Code) throw new InvalidOperationException("Zły kod.");
            return Task.FromResult($"sesja-{email}");
        }

        public Task LogoutAsync(string session, CancellationToken ct)
        {
            Calls.Add($"logout:{session}");
            if (LogoutFails) throw new InvalidOperationException("Brak sieci.");
            return Task.CompletedTask;
        }

        public Task UploadAsync(string session, string folder, string fileName, byte[] data, CancellationToken ct)
        {
            Calls.Add($"upload:{session}");
            Files[(folder, fileName)] = data;
            return Task.CompletedTask;
        }

        public Task<byte[]?> DownloadAsync(string session, string folder, string fileName, CancellationToken ct) =>
            Task.FromResult(Files.GetValueOrDefault((folder, fileName)));

        public Task DeleteAsync(string session, string folder, string fileName, CancellationToken ct)
        {
            Calls.Add($"delete:{session}");
            Files.Remove((folder, fileName));
            return Task.CompletedTask;
        }
    }

    // Rozmówca akcji: zwraca zadaną odpowiedź (null = anulowanie) i zapamiętuje pytania
    private sealed class FakeInteraction(string? answer) : IStorageInteraction
    {
        public List<StoragePrompt> Prompts { get; } = [];

        public Task<string?> AskAsync(StoragePrompt prompt, CancellationToken ct)
        {
            Prompts.Add(prompt);
            return Task.FromResult(answer);
        }
    }

    private MegaStorage Create(CloudMirrorBaseTests.MemorySecrets secrets, FakeMegaClient client)
    {
        var storage = new MegaStorage(secrets, client);
        foreach (var kind in Enum.GetValues<MediaKind>()) storage.Cache.Roots[kind] = Path.Combine(_dir, kind.ToString());
        return storage;
    }

    private static Dictionary<string, string> Credentials(string email, string password) => new() { ["email"] = email, ["password"] = password };

    [Fact]
    public async Task Sign_in_with_email_and_password_stores_only_the_email_and_session()
    {
        var secrets = new CloudMirrorBaseTests.MemorySecrets();
        var storage = Create(secrets, new FakeMegaClient());

        await storage.RunActionAsync("signin", Credentials(Email, Password), StorageInteraction.None);

        Assert.Contains("storage-mega", secrets.Values.Keys);
        Assert.DoesNotContain(secrets.Values.Values, v => v.Contains(Password, StringComparison.Ordinal));
        Assert.Equal("storage.status.signedInAs", storage.Status!.Key);
        Assert.Equal(Email, storage.Status.Detail);
    }

    [Fact]
    public async Task Two_factor_account_asks_for_a_code_and_retries_with_it()
    {
        var client = new FakeMegaClient { TwoFactor = true };
        var storage = Create(new CloudMirrorBaseTests.MemorySecrets(), client);
        var interaction = new FakeInteraction(FakeMegaClient.Code);

        await storage.RunActionAsync("signin", Credentials(Email, Password), interaction);

        Assert.Single(interaction.Prompts);
        Assert.Equal("mfa", interaction.Prompts[0].Key);
        Assert.Equal(2, client.Calls.Count(c => c == "login"));
        Assert.Equal("storage.status.signedInAs", storage.Status!.Key);
    }

    [Fact]
    public async Task Cancelled_code_prompt_leaves_the_user_signed_out()
    {
        var secrets = new CloudMirrorBaseTests.MemorySecrets();
        var storage = Create(secrets, new FakeMegaClient { TwoFactor = true });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            storage.RunActionAsync("signin", Credentials(Email, Password), new FakeInteraction(null)));

        Assert.Equal("storage.status.signedOut", storage.Status!.Key);
        Assert.DoesNotContain("storage-mega", secrets.Values.Keys);
    }

    [Fact]
    public async Task Missing_email_or_password_is_explained()
    {
        var storage = Create(new CloudMirrorBaseTests.MemorySecrets(), new FakeMegaClient());
        var message = Loc.T("storage.mega.missingCredentials");

        var noEmail = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.RunActionAsync("signin", Credentials("", Password), StorageInteraction.None));
        Assert.Equal(message, noEmail.Message);

        var noPassword = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.RunActionAsync("signin", Credentials(Email, ""), StorageInteraction.None));
        Assert.Equal(message, noPassword.Message);
    }

    [Fact]
    public async Task Upload_download_and_delete_use_the_app_folder_and_the_session()
    {
        var client = new FakeMegaClient();
        var storage = Create(new CloudMirrorBaseTests.MemorySecrets(), client);
        await storage.RunActionAsync("signin", Credentials(Email, Password), StorageInteraction.None);

        var path = await storage.WriteAsync(MediaKind.Image, "a.png", new byte[] { 1, 2 });
        await storage.FlushAsync();

        Assert.Equal(new byte[] { 1, 2 }, client.Files[("ImageStudio", "a.png")]);
        Assert.Equal(new byte[] { 1, 2 }, await storage.ReadRemoteAsync("a.png"));
        Assert.Contains($"upload:sesja-{Email}", client.Calls);

        await storage.DeleteAsync(path);
        await storage.FlushAsync();

        Assert.False(client.Files.ContainsKey(("ImageStudio", "a.png")));
        Assert.Contains($"delete:sesja-{Email}", client.Calls);
    }

    [Fact]
    public async Task Sign_out_forgets_the_session_even_if_the_server_logout_fails()
    {
        var secrets = new CloudMirrorBaseTests.MemorySecrets();
        var client = new FakeMegaClient { LogoutFails = true };
        var storage = Create(secrets, client);
        await storage.RunActionAsync("signin", Credentials(Email, Password), StorageInteraction.None);

        await storage.RunActionAsync("signout");

        Assert.Single(client.Calls, c => c.StartsWith("logout:", StringComparison.Ordinal));
        Assert.DoesNotContain("storage-mega", secrets.Values.Keys);
        Assert.Equal("storage.status.signedOut", storage.Status!.Key);
    }

    [Fact]
    public async Task Files_saved_before_sign_in_are_uploaded_after_it()
    {
        var client = new FakeMegaClient();
        var storage = Create(new CloudMirrorBaseTests.MemorySecrets(), client);

        await storage.WriteAsync(MediaKind.Image, "b.png", new byte[] { 9 });
        await storage.FlushAsync();
        Assert.Equal("storage.status.signedOutPending", storage.Status!.Key);
        Assert.Empty(client.Files);

        await storage.RunActionAsync("signin", Credentials(Email, Password), StorageInteraction.None);
        await storage.FlushAsync();

        Assert.Equal(new byte[] { 9 }, client.Files[("ImageStudio", "b.png")]);
        Assert.Empty(storage.PendingOperations);
    }

    [Fact]
    public async Task Password_never_reaches_configure_or_the_persisted_config()
    {
        var secrets = new CloudMirrorBaseTests.MemorySecrets();
        var host = new MediaStorageHost([Create(secrets, new FakeMegaClient())]);
        var tab = new StorageSettingsTab(new SettingsResult("", "", 1024, "", "system"), host);
        var option = tab.Options.Single();
        option.Fields.Single(f => f.Key == "email").Value = Email;
        option.Fields.Single(f => f.Key == "password").Value = Password;

        await option.Actions[0].Command.ExecuteAsync(null);

        var result = tab.Apply(new SettingsResult("", "", 1024, "", "system"));
        Assert.DoesNotContain(Password, JsonSerializer.Serialize(result.StorageConfig));
        Assert.DoesNotContain(Password, JsonSerializer.Serialize(host.WithoutSecrets(result.StorageConfig!)));
        Assert.DoesNotContain(secrets.Values.Values, v => v.Contains(Password, StringComparison.Ordinal));
    }

    [Fact]
    public void Mega_is_discovered_without_changes_in_the_app()
    {
        using var app = TestApp.Create();

        Assert.Contains("mega", app.Get<MediaStorageHost>().Plugins.Select(p => p.Id));
        Assert.Contains(typeof(MegaStorage), StoragePluginDiscovery.PluginTypes(typeof(MegaStorage).Assembly));
    }
}
