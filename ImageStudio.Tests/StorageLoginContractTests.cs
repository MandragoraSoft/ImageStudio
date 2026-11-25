using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using System.Windows.Media;
using ImageStudio.Localization;
using ImageStudio.Services;
using ImageStudio.Storage;
using ImageStudio.ViewModels;
using ImageStudio.Views;

namespace ImageStudio.Tests;

// Kontrakt logowania w zakładce Zapis: pola nietrwałe (hasło), akcja z wartościami i prośba o kod w trakcie akcji
public sealed class StorageLoginContractTests
{
    private static SettingsResult Current() => new("u", "m", 1536, "k", "dark", "openrouter", "or-key");

    // Atrapa wtyczki logowania: zapamiętuje, co dostała w Configure i w akcji
    private sealed class FakeLoginPlugin : IMediaStoragePlugin
    {
        public string Id => "login";
        public string DisplayName => "Logowanie testowe";
        public IReadOnlyList<StorageSetting> Settings { get; } =
        [
            new("email", "storage.cloud.signIn", "jan@example.com", Kind: StorageFieldKind.Email, Transient: true),
            new("password", "storage.cloud.signIn", "", Masked: true, Transient: true),
            new("image", "storage.cloud.signIn", @"C:\Obrazy", IsFolder: true),
        ];
        public IReadOnlyList<StorageAction> Actions { get; } = [new("signin", "storage.cloud.signIn", "storage.status.working")];

        public IReadOnlyDictionary<string, string>? LastConfigure { get; private set; }
        public IReadOnlyDictionary<string, string>? LastInputs { get; private set; }
        public string? LastCode { get; private set; }
        public bool Fail { get; set; }
        public bool AskForCode { get; set; }

        // Liczba aktywnych subskrybentów zdarzenia Status (dodanie +1, zdjęcie -1)
        public int Subscribers { get; private set; }
        public event EventHandler? StatusChanged
        {
            add => Subscribers++;
            remove => Subscribers--;
        }

        public void Configure(IReadOnlyDictionary<string, string> values) => LastConfigure = values;

        public async Task RunActionAsync(string key, IReadOnlyDictionary<string, string> inputs, IStorageInteraction interaction,
                                         CancellationToken ct = default)
        {
            LastInputs = inputs;
            if (Fail) throw new InvalidOperationException("zły login");
            if (AskForCode) LastCode = await interaction.AskAsync(new StoragePrompt("mfa", "storage.prompt.submit"), ct);
        }

        public Task<string> WriteAsync(MediaKind kind, string fileName, ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<byte[]> ReadAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetLocalPathAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // Wtyczka sprzed zmiany: zna tylko starą sygnaturę RunActionAsync
    private sealed class OldPlugin : IMediaStoragePlugin
    {
        public string Id => "old";
        public string DisplayName => "Stara";
        public IReadOnlyList<StorageSetting> Settings => [];
        public string? CalledKey { get; private set; }

        public void Configure(IReadOnlyDictionary<string, string> values) { }
        public Task RunActionAsync(string key, CancellationToken ct = default)
        {
            CalledKey = key;
            return Task.CompletedTask;
        }

        public Task<string> WriteAsync(MediaKind kind, string fileName, ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<byte[]> ReadAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetLocalPathAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // Tworzy zakładkę poza wątkiem UI, żeby kontekst synchronizacji był pusty (zmiany właściwości są natychmiastowe)
    private static Task<StorageOption> CreateOptionAsync(IMediaStoragePlugin plugin) =>
        Task.Run(() => new StorageOption(plugin));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500; i++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail("Warunek nie spełnił się w 5 s.");
    }

    [Fact]
    public void Setting_kind_falls_back_to_the_old_flags()
    {
        Assert.Equal(StorageFieldKind.Folder, new StorageSetting("a", "x", "", IsFolder: true).EffectiveKind);
        Assert.Equal(StorageFieldKind.Password, new StorageSetting("b", "x", "", Masked: true).EffectiveKind);
        Assert.Equal(StorageFieldKind.Text, new StorageSetting("c", "x", "").EffectiveKind);
        Assert.Equal(StorageFieldKind.Email, new StorageSetting("d", "x", "", IsFolder: true, Kind: StorageFieldKind.Email).EffectiveKind);
    }

    [Fact]
    public async Task Action_gets_transient_values_as_inputs_and_configure_never_sees_them()
    {
        using var app = TestApp.Create();
        var plugin = new FakeLoginPlugin();
        var option = await CreateOptionAsync(plugin);
        option.Fields.Single(f => f.Key == "email").Value = "ala@example.com";
        option.Fields.Single(f => f.Key == "password").Value = "tajne";

        await option.Actions[0].Command.ExecuteAsync(null);

        Assert.Equal("ala@example.com", plugin.LastInputs!["email"]);
        Assert.Equal("tajne", plugin.LastInputs!["password"]);
        Assert.False(plugin.LastConfigure!.ContainsKey("email"));
        Assert.False(plugin.LastConfigure!.ContainsKey("password"));
        Assert.Equal(@"C:\Obrazy", plugin.LastConfigure!["image"]);
    }

    [Fact]
    public async Task Transient_fields_are_cleared_after_a_successful_action_and_kept_after_a_failure()
    {
        using var app = TestApp.Create();
        var plugin = new FakeLoginPlugin();
        var option = await CreateOptionAsync(plugin);
        option.Fields.Single(f => f.Key == "password").Value = "tajne";

        plugin.Fail = true;
        await option.Actions[0].Command.ExecuteAsync(null);
        Assert.Equal("tajne", option.Fields.Single(f => f.Key == "password").Value);
        Assert.Equal("zły login", option.ErrorText);

        plugin.Fail = false;
        await option.Actions[0].Command.ExecuteAsync(null);
        Assert.Equal("", option.Fields.Single(f => f.Key == "password").Value);
        Assert.Equal("", option.ErrorText);
    }

    [Fact]
    public async Task Plugin_can_ask_for_a_code_and_gets_the_submitted_value()
    {
        using var app = TestApp.Create();
        var plugin = new FakeLoginPlugin { AskForCode = true };
        var option = await CreateOptionAsync(plugin);

        var run = option.Actions[0].Command.ExecuteAsync(null);
        await WaitUntilAsync(() => option.IsPrompting);
        option.PromptValue = " 123456 ";
        option.SubmitPromptCommand.Execute(null);
        await run;

        Assert.Equal("123456", plugin.LastCode);
        Assert.False(option.IsPrompting);
    }

    [Fact]
    public async Task Cancel_while_asking_for_a_code_ends_the_action_without_error()
    {
        using var app = TestApp.Create();
        var plugin = new FakeLoginPlugin { AskForCode = true };
        var option = await CreateOptionAsync(plugin);

        var run = option.Actions[0].Command.ExecuteAsync(null);
        await WaitUntilAsync(() => option.IsPrompting);
        option.CancelCommand.Execute(null);
        await run;

        Assert.False(option.IsBusy);
        Assert.Equal("", option.ErrorText);
        Assert.False(option.IsPrompting);
    }

    [Fact]
    public void Transient_fields_never_reach_the_settings_result_or_the_persisted_config()
    {
        using var app = TestApp.Create();
        var plugin = new FakeLoginPlugin();
        var host = new MediaStorageHost([plugin]);
        var tab = new StorageSettingsTab(Current(), host);
        tab.Selected!.Fields.Single(f => f.Key == "password").Value = "tajne";

        var result = tab.Apply(Current());
        var persisted = host.WithoutSecrets(result.StorageConfig);

        Assert.False(result.StorageConfig["login"].ContainsKey("email"));
        Assert.False(result.StorageConfig["login"].ContainsKey("password"));
        Assert.Equal(@"C:\Obrazy", result.StorageConfig["login"]["image"]);
        Assert.False(persisted["login"].ContainsKey("password"));
    }

    [Fact]
    public async Task Old_plugin_without_the_new_overload_runs_its_action()
    {
        var plugin = new OldPlugin();
        IMediaStoragePlugin asInterface = plugin;

        await asInterface.RunActionAsync("signin", new Dictionary<string, string> { ["email"] = "x" }, StorageInteraction.None);

        Assert.Equal("signin", plugin.CalledKey);
    }

    [Fact]
    public async Task Open_tab_shows_a_background_upload_error_without_reopening()
    {
        using var app = TestApp.Create();
        var dir = Path.Combine(Path.GetTempPath(), "is-login-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new CloudMirrorBaseTests.TestMirror(new CloudMirrorBaseTests.MemorySecrets());
            foreach (var kind in Enum.GetValues<MediaKind>()) storage.Cache.Roots[kind] = Path.Combine(dir, kind.ToString());
            await storage.RunActionAsync("signin", new Dictionary<string, string> { ["email"] = "ania@example.com" }, StorageInteraction.None);
            storage.FailUploads = 1;
            var option = await CreateOptionAsync(storage);

            await storage.WriteAsync(MediaKind.Image, "c.png", new byte[] { 5 });
            await storage.FlushAsync();

            await WaitUntilAsync(() => option.ErrorText != "");
            Assert.Contains("Serwer niedostępny.", option.ErrorText);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Sign_in_elsewhere_refreshes_the_status_and_buttons()
    {
        using var app = TestApp.Create();
        var storage = new CloudMirrorBaseTests.TestMirror(new CloudMirrorBaseTests.MemorySecrets());
        var option = await CreateOptionAsync(storage);
        Assert.True(option.Actions[0].Command.CanExecute(null));

        await storage.RunActionAsync("signin", new Dictionary<string, string> { ["email"] = "ania@example.com" }, StorageInteraction.None);

        Assert.Equal(Loc.T("storage.status.signedInAs", "ania@example.com"), option.StatusText);
        Assert.False(option.Actions[0].Command.CanExecute(null));
    }

    [Fact]
    public void Disposed_tab_stops_listening_to_plugins()
    {
        using var app = TestApp.Create();
        var plugin = new FakeLoginPlugin();
        var tab = new StorageSettingsTab(Current(), new MediaStorageHost([plugin]));
        Assert.Equal(1, plugin.Subscribers);

        tab.Dispose();

        Assert.Equal(0, plugin.Subscribers);
    }

    [Fact]
    public async Task Settings_dialog_releases_the_storage_tab()
    {
        var plugin = new FakeLoginPlugin();
        using var app = TestApp.Create(services => services.AddSingleton<IMediaStoragePlugin>(plugin));
        var subscribedWhileOpen = -1;
        app.Get<FakeDialogs>().OnShowSettings = (_, _) =>
        {
            subscribedWhileOpen = plugin.Subscribers;
            return null;
        };

        await app.Vm.OpenSettingsCommand.ExecuteAsync(null);

        Assert.Equal(1, subscribedWhileOpen);
        Assert.Equal(0, plugin.Subscribers);
    }

    [Fact]
    public void Storage_view_shows_a_password_box_and_hides_the_prompt()
    {
        using var app = TestApp.Create();
        WpfHost.Run(() =>
        {
            var tab = new StorageSettingsTab(Current(), new MediaStorageHost([new FakeLoginPlugin()]));
            var view = new StorageSettingsTabView { DataContext = tab };
            var window = new Window { Content = view, ShowInTaskbar = false };
            window.Show();
            window.UpdateLayout();

            Assert.NotNull(Find<PasswordBox>(view, _ => true));
            var submit = Loc.T("storage.prompt.submit");
            var button = Find<Button>(view, b => b.Content as string == submit);
            Assert.NotNull(button);
            Assert.False(button!.IsVisible);

            tab.Selected!.IsPrompting = true;
            window.UpdateLayout();
            Assert.True(button.IsVisible);

            window.Close();
        });
    }

    private static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && match(typed)) return typed;
            if (Find(child, match) is { } nested) return nested;
        }
        return null;
    }
}
