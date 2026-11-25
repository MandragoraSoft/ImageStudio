using System.Collections.Concurrent;
using ImageStudio.Configuration;
using ImageStudio.Services;
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

        // Wysłane nazwy plików (w kolejności wysyłek)
        public ConcurrentQueue<string> Uploads { get; } = new();

        // Gdy ustawione, wysyłka czeka na tę bramkę (test nadpisania w trakcie wysyłki); UploadStarted sygnalizuje start
        public TaskCompletionSource? UploadGate { get; set; }
        public TaskCompletionSource UploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Liczba kolejnych wysyłek, które kończą się błędem (zdalny serwer niedostępny)
        public int FailUploads { get; set; }

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

        protected override async Task UploadAsync(string accessToken, string fileName, byte[] data, CancellationToken ct)
        {
            UploadStarted.TrySetResult();
            if (UploadGate is { } gate) await gate.Task;
            if (FailUploads > 0)
            {
                FailUploads--;
                throw new InvalidOperationException("Serwer niedostępny.");
            }
            Uploads.Enqueue(fileName);
            Remote[fileName] = data;
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
    public async Task File_saved_while_signed_out_is_uploaded_after_sign_in()
    {
        var storage = Mirror(new TestMirror(new MemorySecrets()));
        var path = await storage.WriteAsync(MediaKind.Image, "b.png", new byte[] { 3 });
        await storage.FlushAsync();

        Assert.True(File.Exists(path));
        Assert.Empty(storage.Remote);
        Assert.Equal("storage.status.signedOutPending", storage.Status!.Key);
        Assert.Equal("1", storage.Status!.Detail);

        await storage.RunActionAsync("signin", Email("ania@example.com"), StorageInteraction.None);
        await storage.FlushAsync();

        Assert.Equal(new byte[] { 3 }, storage.Remote["b.png"]);
        Assert.Empty(storage.PendingOperations);
    }

    [Fact]
    public async Task Pending_uploads_survive_a_restart()
    {
        var secrets = new MemorySecrets();
        var before = Mirror(new TestMirror(secrets));
        await before.WriteAsync(MediaKind.Image, "c.png", new byte[] { 5 });
        await before.FlushAsync();

        // Nowa instancja na tych samych sekretach (po restarcie aplikacji)
        var after = Mirror(new TestMirror(secrets));
        Assert.Single(after.PendingOperations);
        await after.RunActionAsync("signin", Email("ania@example.com"), StorageInteraction.None);
        await after.FlushAsync();

        Assert.Equal(new byte[] { 5 }, after.Remote["c.png"]);
        Assert.Empty(after.PendingOperations);
    }

    [Fact]
    public async Task Delete_while_signed_out_replaces_the_pending_upload_with_a_remote_delete()
    {
        var storage = Mirror(new TestMirror(new MemorySecrets()));
        var path = await storage.WriteAsync(MediaKind.Image, "e.png", new byte[] { 1 });
        await storage.DeleteAsync(path);
        await storage.FlushAsync();

        var op = Assert.Single(storage.PendingOperations);
        Assert.Equal("delete", op.Op);

        await storage.RunActionAsync("signin", Email("ania@example.com"), StorageInteraction.None);
        await storage.FlushAsync();

        Assert.Empty(storage.Uploads);
        Assert.Empty(storage.PendingOperations);
    }

    [Fact]
    public async Task Failed_upload_stays_pending_and_is_retried_on_resume()
    {
        var storage = Mirror(new TestMirror(new MemorySecrets()));
        await storage.RunActionAsync("signin", Email("ania@example.com"), StorageInteraction.None);
        storage.FailUploads = 1;

        await storage.WriteAsync(MediaKind.Image, "f.png", new byte[] { 2 });
        await storage.FlushAsync();

        Assert.Single(storage.PendingOperations);
        Assert.Equal("storage.status.error", storage.Status!.Key);

        storage.Resume();
        await storage.FlushAsync();

        Assert.Equal(new byte[] { 2 }, storage.Remote["f.png"]);
        Assert.Empty(storage.PendingOperations);
    }

    [Fact]
    public async Task Pending_entry_of_a_missing_local_file_is_dropped()
    {
        var storage = Mirror(new TestMirror(new MemorySecrets()));
        var path = await storage.WriteAsync(MediaKind.Image, "g.png", new byte[] { 4 });
        File.Delete(path);

        await storage.RunActionAsync("signin", Email("ania@example.com"), StorageInteraction.None);
        await storage.FlushAsync();

        Assert.Empty(storage.Uploads);
        Assert.Empty(storage.PendingOperations);
    }

    [Fact]
    public async Task Double_resume_uploads_once()
    {
        var storage = Mirror(new TestMirror(new MemorySecrets()));
        await storage.WriteAsync(MediaKind.Image, "h.png", new byte[] { 6 });

        await storage.RunActionAsync("signin", Email("ania@example.com"), StorageInteraction.None);
        storage.Resume();
        await storage.FlushAsync();

        Assert.Single(storage.Uploads);
        Assert.Empty(storage.PendingOperations);
    }

    [Fact]
    public async Task Overwrite_while_an_upload_is_in_flight_still_uploads_the_latest_content()
    {
        var storage = Mirror(new TestMirror(new MemorySecrets()) { UploadGate = new TaskCompletionSource() });
        await storage.RunActionAsync("signin", Email("ania@example.com"), StorageInteraction.None);

        // Pierwsza wysyłka "a" startuje i blokuje się na bramce; drugi zapis tej samej nazwy trafia do kolejki
        await storage.WriteAsync(MediaKind.Image, "x.png", new byte[] { (byte)'a' });
        await storage.UploadStarted.Task;
        await storage.WriteAsync(MediaKind.Image, "x.png", new byte[] { (byte)'b' });

        storage.UploadGate!.TrySetResult();
        await storage.FlushAsync();

        Assert.Equal(new byte[] { (byte)'b' }, storage.Remote["x.png"]);
        Assert.Empty(storage.PendingOperations);
    }

    private sealed class ResumeCounter(string id) : IMediaStoragePlugin
    {
        public int Resumed { get; private set; }

        public string Id => id;
        public string DisplayName => id;
        public IReadOnlyList<StorageSetting> Settings => [];
        public void Configure(IReadOnlyDictionary<string, string> values) { }
        public void Resume() => Resumed++;

        public Task<string> WriteAsync(MediaKind kind, string fileName, ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<byte[]> ReadAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetLocalPathAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public void Host_load_resumes_every_plugin()
    {
        var first = new ResumeCounter("first");
        var second = new ResumeCounter("second");

        new MediaStorageHost([first, second]).Load(new UserState());

        Assert.Equal(1, first.Resumed);
        Assert.Equal(1, second.Resumed);
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
