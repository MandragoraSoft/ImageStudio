using ImageStudio.Configuration;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

public sealed class UserDataCleanupTests
{
    [Fact]
    public void Settings_category_defaults_select_everything_except_provider_credentials()
    {
        var tab = new AppSettingsTab(new SettingsResult("url", "market", 1024, "", "system"));

        Assert.True(tab.ClearSettingsAndPrompts);
        Assert.True(tab.ClearChatHistoryAndMemory);
        Assert.True(tab.ClearPresets);
        Assert.True(tab.ClearGalleryIndexes);
        Assert.True(tab.ClearMcpConfiguration);
        Assert.True(tab.ClearWindowLayouts);
        Assert.False(tab.ClearProviderLoginsAndKeys);
    }

    [Fact]
    public void Default_selection_clears_only_known_app_data_and_preserves_media_unknown_files_and_credentials()
    {
        using var root = new IsolatedDataDirectory();
        root.Write("settings.json");
        root.Write("state.json");
        root.Write("models.json");
        root.Write("memory.json");
        root.Write("prompts.json");
        root.Write("sessions\\abcdef.json");
        root.Write("sessions\\abcdef.meta.json");
        root.Write("sessions\\notes.json");
        root.Write("prompt-chats\\123456.json");
        root.Write("presets\\20261112-123456-0123456789abcde.json", """{"Name":"a","Kind":"generate","Prompt":"x"}""");
        root.Write("presets\\20261031-134522-3fa9c1.json", """{"Name":"b","Kind":"edit","Prompt":"y"}""");
        root.Write("presets\\unknown.json");
        root.Write("presets\\keep.txt");
        root.Write("gallery.json", """[{"FilePath":"media\\photo.png"}]""");
        root.Write("voice.json");
        root.Write("mcp.json");
        root.Write("secrets-mcp-server.bin");
        root.Write("windows.json");
        root.Write("secrets.bin");
        root.Write("secrets-chatgpt.bin");
        root.Write("secrets-storage-onedrive.bin");
        root.Write("secrets-custom.bin");
        root.Write(".env");
        root.Write("media\\photo.png", "local media");

        var result = new UserDataCleanupService(root.Path).Clean(new UserDataCleanupSelection());

        Assert.Equal(15, result.DeletedFiles);
        Assert.False(root.Exists("settings.json"));
        Assert.False(root.Exists("state.json"));
        Assert.False(root.Exists("models.json"));
        Assert.False(root.Exists("memory.json"));
        Assert.False(root.Exists("prompts.json"));
        Assert.False(root.Exists("sessions\\abcdef.json"));
        Assert.False(root.Exists("sessions\\abcdef.meta.json"));
        Assert.False(root.Exists("prompt-chats\\123456.json"));
        Assert.False(root.Exists("presets\\20261112-123456-0123456789abcde.json"));
        Assert.False(root.Exists("presets\\20261031-134522-3fa9c1.json"));
        Assert.False(root.Exists("gallery.json"));
        Assert.False(root.Exists("voice.json"));
        Assert.False(root.Exists("mcp.json"));
        Assert.False(root.Exists("secrets-mcp-server.bin"));
        Assert.False(root.Exists("windows.json"));
        Assert.True(root.Exists("sessions\\notes.json"));
        Assert.True(root.Exists("presets\\unknown.json"));
        Assert.True(root.Exists("presets\\keep.txt"));
        Assert.True(root.Exists("secrets.bin"));
        Assert.True(root.Exists("secrets-chatgpt.bin"));
        Assert.True(root.Exists("secrets-storage-onedrive.bin"));
        Assert.True(root.Exists("secrets-custom.bin"));
        Assert.True(root.Exists(".env"));
        Assert.Equal("local media", File.ReadAllText(root.File("media\\photo.png")));
        Assert.False(UserDataMutationGate.TryWrite(root.Path, root.File("state.json"), () =>
            File.WriteAllText(root.File("state.json"), "recreated")));
        Assert.True(UserDataMutationGate.TryWrite(root.Path, root.File("secrets.bin"), () =>
            File.WriteAllText(root.File("secrets.bin"), "kept")));
        Assert.False(UserDataMutationGate.TryWrite(root.Path, root.File("models.json"), () =>
            File.WriteAllText(root.File("models.json"), "recreated")));
        Assert.False(UserDataMutationGate.TryWrite(root.Path, root.File("prompts.json"), () =>
            File.WriteAllText(root.File("prompts.json"), "recreated")));
    }

    [Fact]
    public void Preset_cleanup_uses_content_not_filename_and_preserves_ambiguous_and_invalid_files()
    {
        using var root = new IsolatedDataDirectory();
        root.Write("presets\\moj zestaw.json", """{"Name":"Mój","Kind":"edit","Prompt":"obraz"}""");
        root.Write("presets\\20261031-134522-3fa9c1.json", "{uszkodzony");
        root.Write("presets\\obcy.json", """{"notatka":"nie usuwać"}""");
        root.Write("presets\\pusty.json", "null");
        root.Write("presets\\zestaw.txt", """{"Name":"x","Kind":"edit","Prompt":"x"}""");
        root.Write("presets\\podfolder\\zestaw.json", """{"Name":"x","Kind":"edit","Prompt":"x"}""");
        var selection = new UserDataCleanupSelection(false, false, true, false, false, false);

        var error = Assert.Throws<UserDataCleanupException>(() =>
            new UserDataCleanupService(root.Path).Clean(selection));

        Assert.False(root.Exists("presets\\moj zestaw.json"));
        Assert.Equal("{uszkodzony", File.ReadAllText(root.File("presets\\20261031-134522-3fa9c1.json")));
        Assert.Equal("""{"notatka":"nie usuwać"}""", File.ReadAllText(root.File("presets\\obcy.json")));
        Assert.True(root.Exists("presets\\pusty.json"));
        Assert.True(root.Exists("presets\\zestaw.txt"));
        Assert.True(root.Exists("presets\\podfolder\\zestaw.json"));
        Assert.Contains(error.Failures, failure => failure.Contains("obcy.json"));
        Assert.True(UserDataMutationGate.IsBlocked(root.Path, root.File("presets\\moj zestaw.json")));
    }

    [Fact]
    public void Preset_with_non_generated_name_is_removed_without_failure()
    {
        using var root = new IsolatedDataDirectory();
        root.Write("presets\\wlasny.json", """{"Name":"Mój","Kind":"generate","Prompt":"obraz"}""");
        root.Write("presets\\inne.txt", "pozostaje");
        var selection = new UserDataCleanupSelection(false, false, true, false, false, false);

        var result = new UserDataCleanupService(root.Path).Clean(selection);

        Assert.Equal(1, result.DeletedFiles);
        Assert.False(root.Exists("presets\\wlasny.json"));
        Assert.Equal("pozostaje", File.ReadAllText(root.File("presets\\inne.txt")));
    }

    [Fact]
    public void Preset_cleanup_does_not_read_or_delete_a_link()
    {
        using var root = new IsolatedDataDirectory();
        using var outside = new IsolatedDataDirectory();
        outside.Write("zestaw.json", """{"Name":"x","Kind":"generate","Prompt":"zachowaj"}""");
        Directory.CreateDirectory(root.File("presets"));
        var link = root.File("presets\\link.json");
        try { File.CreateSymbolicLink(link, outside.File("zestaw.json")); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var selection = new UserDataCleanupSelection(false, false, true, false, false, false);
        Assert.Throws<UserDataCleanupException>(() => new UserDataCleanupService(root.Path).Clean(selection));
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        Assert.Equal("""{"Name":"x","Kind":"generate","Prompt":"zachowaj"}""",
            File.ReadAllText(outside.File("zestaw.json")));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Settings_and_history_only_remove_and_block_their_own_files(bool settings, bool history)
    {
        using var root = new IsolatedDataDirectory();
        root.Write("models.json", "models");
        root.Write("prompts.json", "history");
        var selection = new UserDataCleanupSelection(settings, history, false, false, false, false);

        new UserDataCleanupService(root.Path).Clean(selection);

        Assert.Equal(!settings, root.Exists("models.json"));
        Assert.Equal(!history, root.Exists("prompts.json"));
        Assert.Equal(settings, UserDataMutationGate.IsBlocked(root.Path, root.File("models.json")));
        Assert.Equal(history, UserDataMutationGate.IsBlocked(root.Path, root.File("prompts.json")));
        var prefs = new ModelPreferences { PathOverride = root.File("models.json") };
        prefs.For("test").PreferredOnly = true;
        prefs.Save();
        Assert.Equal(!settings, root.Exists("models.json"));
        if (!history) Assert.Equal("history", File.ReadAllText(root.File("prompts.json")));
    }

    [Fact]
    public void Provider_credentials_are_opt_in_and_only_known_secret_files_are_removed()
    {
        using var root = new IsolatedDataDirectory();
        foreach (var name in new[]
                 {
                     "secrets.bin", "secrets-openrouter.bin", "secrets-openai.bin", "secrets-mistral.bin",
                     "secrets-kilo.bin", "secrets-opencodego.bin", "secrets-chatgpt.bin",
                     "secrets-storage-onedrive.bin", "secrets-storage-onedrive-client.bin",
                     "secrets-storage-onedrive-clientid.bin", "secrets-storage-googledrive.bin",
                     "secrets-storage-googledrive-client.bin", "secrets-storage-googledrive-clientid.bin",
                     "secrets-custom.bin", "settings.json",
                 })
            root.Write(name);

        var defaultResult = new UserDataCleanupService(root.Path).Clean(new UserDataCleanupSelection(
            SettingsAndPrompts: false, ChatHistoryAndMemory: false, Presets: false, GalleryIndexes: false,
            McpConfiguration: false, WindowLayouts: false));

        Assert.Equal(0, defaultResult.DeletedFiles);
        Assert.True(root.Exists("secrets.bin"));

        new UserDataCleanupService(root.Path).Clean(new UserDataCleanupSelection(
            SettingsAndPrompts: false, ChatHistoryAndMemory: false, Presets: false, GalleryIndexes: false,
            McpConfiguration: false, WindowLayouts: false, ProviderLoginsAndKeys: true));

        Assert.False(root.Exists("secrets.bin"));
        Assert.False(root.Exists("secrets-chatgpt.bin"));
        Assert.False(root.Exists("secrets-storage-onedrive.bin"));
        Assert.False(root.Exists("secrets-storage-googledrive-clientid.bin"));
        Assert.True(root.Exists("secrets-custom.bin"));
        Assert.True(root.Exists("settings.json"));
    }

    [Fact]
    public void Cleanup_reports_locked_file_failures_and_leaves_the_target_write_blocked()
    {
        using var root = new IsolatedDataDirectory();
        root.Write("settings.json", "original");
        using var locked = new FileStream(root.File("settings.json"), FileMode.Open, FileAccess.Read, FileShare.None);

        var error = Assert.Throws<UserDataCleanupException>(() =>
            new UserDataCleanupService(root.Path).Clean(new UserDataCleanupSelection(
                ChatHistoryAndMemory: false, Presets: false, GalleryIndexes: false,
                McpConfiguration: false, WindowLayouts: false)));

        Assert.NotEmpty(error.Failures);
        Assert.True(root.Exists("settings.json"));
        Assert.True(UserDataMutationGate.IsBlocked(root.Path, root.File("settings.json")));
        Assert.False(UserDataMutationGate.TryWrite(root.Path, root.File("settings.json"), () => { }));
    }

    [Fact]
    public void Reparse_point_directory_is_not_traversed()
    {
        using var root = new IsolatedDataDirectory();
        using var outside = new IsolatedDataDirectory();
        outside.Write("abcdef.json", "outside");
        var link = root.File("sessions");
        try { Directory.CreateSymbolicLink(link, outside.Path); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        Assert.Throws<UserDataCleanupException>(() => new UserDataCleanupService(root.Path).Clean(
            new UserDataCleanupSelection(SettingsAndPrompts: false, Presets: false, GalleryIndexes: false,
                McpConfiguration: false, WindowLayouts: false, ProviderLoginsAndKeys: false)));

        Assert.Equal("outside", File.ReadAllText(outside.File("abcdef.json")));
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    [Fact]
    public async Task Declining_confirmation_does_not_delete_or_block_writes()
    {
        using var app = TestApp.Create();
        app.Dialogs.ConfirmAnswer = false;
        app.WriteData("settings.json", "keep");
        var tab = await OpenApplicationSettingsAsync(app);

        await tab.ClearSelectedDataCommand.ExecuteAsync(null);

        Assert.Equal(1, app.Dialogs.ConfirmCount);
        Assert.Equal(0, app.Lifetime.ExitCount);
        Assert.Contains("token", app.Dialogs.LastConfirmationText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chmurze", app.Dialogs.LastConfirmationText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(app.DataDirectory, "settings.json")));
        Assert.False(UserDataMutationGate.IsBlocked(app.DataDirectory, Path.Combine(app.DataDirectory, "settings.json")));
    }

    [Fact]
    public async Task Successful_cleanup_exits_and_does_not_restore_state_or_secrets()
    {
        using var app = TestApp.Create();
        app.WriteData("settings.json", "settings");
        app.WriteData("state.json", "state");
        app.WriteData("secrets.bin", "secret");
        app.WriteData("gallery.json", """[{"FilePath":"media\\photo.png"}]""");
        app.WriteData("media\\photo.png", "media");
        var tab = await OpenApplicationSettingsAsync(app);
        tab.ClearChatHistoryAndMemory = false;
        tab.ClearPresets = false;
        tab.ClearGalleryIndexes = false;
        tab.ClearMcpConfiguration = false;
        tab.ClearWindowLayouts = false;

        await tab.ClearSelectedDataCommand.ExecuteAsync(null);
        app.Vm.Shutdown();

        Assert.Equal(1, app.Lifetime.ExitCount);
        Assert.False(File.Exists(Path.Combine(app.DataDirectory, "settings.json")));
        Assert.False(File.Exists(Path.Combine(app.DataDirectory, "state.json")));
        Assert.True(File.Exists(Path.Combine(app.DataDirectory, "secrets.bin")));
        Assert.True(File.Exists(Path.Combine(app.DataDirectory, "gallery.json")));
        Assert.Equal("media", File.ReadAllText(Path.Combine(app.DataDirectory, "media\\photo.png")));
    }

    [Fact]
    public async Task Partial_cleanup_failure_is_reported_then_exits_without_recreating_deleted_files()
    {
        using var app = TestApp.Create();
        app.WriteData("settings.json", "locked");
        app.WriteData("models.json", "models");
        app.WriteData("gallery.json", "keep");
        var tab = await OpenApplicationSettingsAsync(app);
        tab.ClearChatHistoryAndMemory = false;
        tab.ClearPresets = false;
        tab.ClearGalleryIndexes = false;
        tab.ClearMcpConfiguration = false;
        tab.ClearWindowLayouts = false;
        using var locked = new FileStream(Path.Combine(app.DataDirectory, "settings.json"),
            FileMode.Open, FileAccess.Read, FileShare.None);

        await tab.ClearSelectedDataCommand.ExecuteAsync(null);

        Assert.Equal(1, app.Lifetime.ExitCount);
        Assert.NotNull(app.Dialogs.LastErrorText);
        Assert.Contains("settings.json", app.Dialogs.LastErrorText);
        Assert.Contains("zakończy działanie", app.Dialogs.LastErrorText);
        Assert.True(File.Exists(Path.Combine(app.DataDirectory, "settings.json")));
        Assert.False(File.Exists(Path.Combine(app.DataDirectory, "models.json")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(app.DataDirectory, "gallery.json")));
        app.Get<ModelPreferences>().Save();
        app.Vm.Shutdown();
        Assert.False(File.Exists(Path.Combine(app.DataDirectory, "models.json")));
    }

    [Fact]
    public async Task Stop_failure_exits_without_deleting_and_preserves_unselected_state()
    {
        using var app = TestApp.Create();
        await app.Vm.InitializeAsync();
        app.Vm.GeneratePrompt = "zachowany prompt";
        app.State.State.GeneratePrompt = "stary stan";
        app.WriteData("memory.json", "keep");
        var tab = await OpenApplicationSettingsAsync(app);
        tab.ClearSettingsAndPrompts = false;
        tab.ClearPresets = false;
        tab.ClearGalleryIndexes = false;
        tab.ClearMcpConfiguration = false;
        tab.ClearWindowLayouts = false;
        app.Provider.OnDispose = () =>
        {
            app.Provider.OnDispose = null;
            throw new InvalidOperationException("stop failed");
        };

        await tab.ClearSelectedDataCommand.ExecuteAsync(null);

        Assert.Equal(1, app.Lifetime.ExitCount);
        Assert.Contains("stop failed", app.Dialogs.LastErrorText);
        Assert.Contains("Nie rozpoczęto usuwania", app.Dialogs.LastErrorText);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(app.DataDirectory, "memory.json")));
        Assert.Equal("zachowany prompt", app.State.State.GeneratePrompt);
        Assert.False(UserDataMutationGate.IsBlocked(app.DataDirectory, Path.Combine(app.DataDirectory, "state.json")));
        app.Vm.Shutdown();
        Assert.Equal("zachowany prompt", app.State.State.GeneratePrompt);
    }

    private static async Task<AppSettingsTab> OpenApplicationSettingsAsync(TestApp app)
    {
        AppSettingsTab? tab = null;
        app.Dialogs.OnShowSettings = (_, tabs) =>
        {
            tab = Assert.IsType<AppSettingsTab>(tabs.Single(t => t is AppSettingsTab));
            return null;
        };
        await app.Vm.OpenSettingsCommand.ExecuteAsync(null);
        return tab ?? throw new InvalidOperationException("Zakładka Aplikacja nie została otwarta.");
    }

    private sealed class IsolatedDataDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ImageStudio-CleanupTests", Guid.NewGuid().ToString("N"));

        public IsolatedDataDirectory() => Directory.CreateDirectory(Path);
        public string File(string relativePath) => System.IO.Path.Combine(Path, relativePath);
        public bool Exists(string relativePath) => System.IO.File.Exists(File(relativePath));
        public void Write(string relativePath, string content = "test")
        {
            var path = File(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
