using System.Collections.Concurrent;
using ImageStudio.Storage;
using ImageStudio.Storage.Cloud;
using ImageStudio.Storage.GoogleDrive;
using ImageStudio.Storage.OneDrive;

namespace ImageStudio.Tests;

public sealed class CloudMirrorBaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "is-mirror-" + Guid.NewGuid().ToString("N"));

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

    // Wtyczka z logowaniem poświadczeniami (jak MEGA): sesją jest e-mail zapisany w sekrecie
    private sealed class TestMirror(IStorageSecrets secrets) : CloudMirrorStorage(secrets)
    {
        private const string SessionName = "storage-test";

        public ConcurrentDictionary<string, byte[]> Remote { get; } = new();

        public override string Id => "test";
        public override string DisplayName => "Test";
        public override string? HelpKey => null;

        protected override bool SignedIn => Secrets.Load(SessionName) is not null;
        protected override string? AccountLabel => Secrets.Load(SessionName);

        protected override Task<string?> AccessTokenAsync(CancellationToken ct) => Task.FromResult(Secrets.Load(SessionName));

        protected override Task SignInAsync(IReadOnlyDictionary<string, string> inputs, IStorageInteraction interaction, CancellationToken ct)
        {
            Secrets.Save(inputs["email"], SessionName);
            return Task.CompletedTask;
        }

        protected override Task SignOutAsync(CancellationToken ct)
        {
            Secrets.Save("", SessionName);
            return Task.CompletedTask;
        }

        protected override Task UploadAsync(string accessToken, string fileName, byte[] data, CancellationToken ct)
        {
            Remote[fileName] = data;
            return Task.CompletedTask;
        }

        protected override Task DeleteRemoteAsync(string accessToken, string fileName, CancellationToken ct)
        {
            Remote.TryRemove(fileName, out _);
            return Task.CompletedTask;
        }

        protected override Task<byte[]?> DownloadRemoteAsync(string accessToken, string fileName, CancellationToken ct) =>
            Task.FromResult(Remote.TryGetValue(fileName, out var bytes) ? bytes : null);
    }

    private T Mirror<T>(T plugin) where T : CloudMirrorStorage
    {
        foreach (var kind in Enum.GetValues<MediaKind>()) plugin.Cache.Roots[kind] = Path.Combine(_dir, kind.ToString());
        return plugin;
    }

    private static Dictionary<string, string> Email(string email) => new() { ["email"] = email };

    [Fact]
    public async Task Credential_mirror_signs_in_with_inputs_and_uploads_in_the_background()
    {
        var storage = Mirror(new TestMirror(new MemorySecrets()));

        await storage.RunActionAsync("signin", Email("ania@example.com"), StorageInteraction.None);
        var path = await storage.WriteAsync(MediaKind.Image, "a.png", new byte[] { 7 });
        await storage.FlushAsync();

        Assert.True(File.Exists(path));
        Assert.Equal(new byte[] { 7 }, storage.Remote["a.png"]);
    }

    [Fact]
    public async Task Sign_out_clears_the_session_and_the_status_says_signed_out()
    {
        var storage = Mirror(new TestMirror(new MemorySecrets()));
        await storage.RunActionAsync("signin", Email("ania@example.com"), StorageInteraction.None);

        await storage.RunActionAsync("signout");

        Assert.Equal("storage.status.signedOut", storage.Status!.Key);
        Assert.True(storage.CanRun("signin"));
        Assert.False(storage.CanRun("signout"));
    }

    [Fact]
    public async Task Status_shows_the_account_label_after_sign_in()
    {
        var storage = Mirror(new TestMirror(new MemorySecrets()));

        await storage.RunActionAsync("signin", Email("ania@example.com"), StorageInteraction.None);

        Assert.Equal("storage.status.signedInAs", storage.Status!.Key);
        Assert.Equal("ania@example.com", storage.Status!.Detail);
    }

    [Fact]
    public void OAuth_plugins_keep_their_fields_and_actions()
    {
        var onedrive = Mirror(new OneDriveStorage(new MemorySecrets()));
        var google = Mirror(new GoogleDriveStorage(new MemorySecrets()));

        Assert.Equal(new[] { "clientid", "image", "video", "audio" }, onedrive.Settings.Select(s => s.Key).ToArray());
        Assert.Equal(new[] { "clientid", "clientsecret", "image", "video", "audio" }, google.Settings.Select(s => s.Key).ToArray());
        Assert.Equal(
            new[] { ("signin", "storage.cloud.signIn"), ("signout", "storage.cloud.signOut") },
            onedrive.Actions.Select(a => (a.Key, a.LabelKey)).ToArray());
        Assert.Equal(
            new[] { ("signin", "storage.cloud.signIn"), ("signout", "storage.cloud.signOut") },
            google.Actions.Select(a => (a.Key, a.LabelKey)).ToArray());
    }
}
