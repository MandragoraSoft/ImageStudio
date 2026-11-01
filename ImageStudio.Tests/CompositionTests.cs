using ImageStudio.Composition;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using ImageStudio.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ImageStudio.Tests;

// Kontener DI: produkcyjny composition root składa się poprawnie, a klasy aplikacji dostają magazyny z kontenera
public sealed class CompositionTests
{
    private static ServiceProvider Production() =>
        new ServiceCollection().AddImageStudioCore().AddImageStudioInfrastructure()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    // McpToolHost implementuje tylko IAsyncDisposable, więc kontener zwalniamy asynchronicznie
    private sealed class Scope(ServiceProvider services) : IDisposable
    {
        public ServiceProvider Services => services;
        public void Dispose() => services.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public void Production_container_validates_and_resolves_the_main_view_model_with_all_tabs()
    {
        Sta.Run(() =>
        {
            using var scope = new Scope(Production());
            var services = scope.Services;

            var vm = services.GetRequiredService<MainViewModel>();

            Assert.Same(vm, services.GetRequiredService<MainViewModel>());  // singleton
            Assert.IsType<WpfDialogService>(services.GetRequiredService<IDialogService>());
            Assert.IsType<FilePresetStore>(services.GetRequiredService<IPresetStore>());
            Assert.Equal(["Generowanie", "Edycja", "Wideo", "Czat", "Głos"],
                         services.GetServices<IMainTab>().OrderBy(t => t.Order).Select(t => t.Title));
        });
    }

    [Fact]
    public void Production_container_builds_the_real_main_window_with_every_tab_view()
    {
        WpfHost.Run(() =>
        {
            using var scope = new Scope(Production());
            var services = scope.Services;

            var window = services.GetRequiredService<ImageStudio.MainWindow>();

            var tabs = (System.Windows.Controls.TabControl)window.FindName("MainTabs");
            Assert.Equal(5, tabs.Items.Count);
            Assert.Same(services.GetRequiredService<MainViewModel>(), window.DataContext);
            window.Close();
        });
    }

    [Fact]
    public void Test_container_uses_in_memory_stores_and_fakes_instead_of_infrastructure()
    {
        using var app = TestApp.Create();

        Assert.IsType<MemoryPresetStore>(app.Get<IPresetStore>());
        Assert.IsType<MemoryUserStateStore>(app.Get<IUserStateStore>());
        Assert.Same(app.Dialogs, app.Get<IDialogService>());
        Assert.Same(app.Provider, app.Get<IAiProvider>());
    }

    [Fact]
    public void State_is_saved_to_and_restored_from_the_injected_store()
    {
        Sta.Run(() =>
        {
            var state = new MemoryUserStateStore();
            using (var first = TestApp.Create(s => s.AddSingleton<IUserStateStore>(state)))
            {
                var vm = first.Vm;
                vm.InitializeAsync().GetAwaiter().GetResult();
                vm.GeneratePrompt = "kot w kapeluszu";
                vm.SelectedQuality = "high";
            }
            Assert.Equal("kot w kapeluszu", state.State.GeneratePrompt);

            using var second = TestApp.Create(s => s.AddSingleton<IUserStateStore>(state));

            Assert.Equal("kot w kapeluszu", second.Vm.GeneratePrompt);
            Assert.Equal("high", second.Vm.SelectedQuality);
        });
    }

    [Fact]
    public void Gallery_is_loaded_from_and_cleared_in_the_injected_store()
    {
        Sta.Run(() =>
        {
            var gallery = new MemoryGalleryStore();
            gallery.Entries.Add(new GalleryEntry { Prompt = "stary", FilePath = "nie-ma.png" });
            using var app = TestApp.Create(s => s.AddSingleton<IGalleryStore>(gallery));
            var vm = app.Vm;
            Assert.Single(vm.Gallery);

            vm.ClearGalleryCommand.Execute(null);

            Assert.Empty(vm.Gallery);
            Assert.Empty(gallery.Entries);
        });
    }

    [Fact]
    public void Saved_preset_lands_in_the_injected_preset_store()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            var vm = app.Vm;
            vm.InitializeAsync().GetAwaiter().GetResult();
            app.Dialogs.SavePresetAnswer = (_, _) => new PresetDraft("moj zestaw", "opis");
            vm.GeneratePrompt = "zachód słońca";

            vm.SavePresetCommand.Execute(null);

            var preset = Assert.Single(app.Presets.Presets);
            Assert.Equal("moj zestaw", preset.Name);
            Assert.Equal("zachód słońca", preset.Prompt);
            Assert.False(vm.PresetDirty);
        });
    }

    [Fact]
    public void Settings_dialog_reads_secrets_and_saves_through_the_injected_stores()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            app.Secrets.Secrets["openrouter"] = "klucz-or";
            SettingsResult? shown = null;
            app.Dialogs.SettingsAnswer = current =>
            {
                shown = current;
                return current with { MaxSide = 1024, Theme = "dark" };
            };
            var vm = app.Vm;
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.OpenSettingsCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Equal("klucz-or", shown!.OpenRouterKey);
            Assert.Equal(1, app.Settings.SaveCalls);
            Assert.Equal(1024, app.Settings.Settings.MaxSide);
            Assert.Equal("dark", app.Settings.Settings.Theme);
            Assert.Equal(["dark"], ((FakeThemeService)app.Get<IThemeService>()).Applied);
        });
    }

    [Fact]
    public void Changing_the_provider_in_settings_asks_the_injected_factory_for_a_new_one()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            var factory = (FakeProviderFactory)app.Get<IProviderFactory>();
            var vm = app.Vm;
            vm.InitializeAsync().GetAwaiter().GetResult();

            app.Dialogs.SettingsAnswer = current => current with { Theme = "light" };
            vm.OpenSettingsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Empty(factory.Created);  // sam motyw nie tworzy dostawcy

            app.Dialogs.SettingsAnswer = current => current with { ProviderId = "openrouter" };
            vm.OpenSettingsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Equal(["openrouter"], factory.Created);
        });
    }

    [Fact]
    public void Legacy_importer_writes_into_the_injected_preset_store_and_skips_duplicates()
    {
        using var app = TestApp.Create();
        Directory.CreateDirectory(AppPaths.UserDataDir);
        var path = Path.Combine(AppPaths.UserDataDir, "prompts.json");
        File.WriteAllText(path, """{"Generate":["kot","pies"],"Edit":["usuń tło"]}""");
        try
        {
            var importer = app.Get<LegacyPromptImporter>();

            var first = importer.Import();
            var second = importer.Import();

            Assert.Equal(3, first.Imported);
            Assert.Equal(0, second.Imported);
            Assert.Equal(3, second.Skipped);
            Assert.Equal(3, app.Presets.Presets.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Mcp_server_token_comes_from_and_is_kept_in_the_injected_secret_store()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            var vm = app.Vm;

            var token = vm.Tools.Server!.ClientConfig;

            var stored = Assert.Contains("mcp-server", app.Secrets.Secrets);
            Assert.Contains(stored, token);
        });
    }

    [Fact]
    public void File_stores_round_trip_through_the_isolated_data_directory()
    {
        var presets = new FilePresetStore();
        var created = presets.Create(new Preset { Name = "plikowy", Kind = "edit", Prompt = "p" });
        Assert.Contains(presets.LoadAll(), p => p.Name == "plikowy");
        presets.Delete(created);
        Assert.DoesNotContain(presets.LoadAll(), p => p.Name == "plikowy");

        var state = new FileUserStateStore();
        state.Save(new UserState { GeneratePrompt = "z pliku" });
        Assert.Equal("z pliku", state.Load().GeneratePrompt);

        var secrets = new FileSecretStore();
        secrets.Save("tajne", "test-di");
        Assert.Equal("tajne", secrets.Load("test-di"));
        secrets.Save("", "test-di");
        Assert.Null(secrets.Load("test-di"));

        Assert.NotNull(new FileSettingsStore().Load().BaseUrl);
        Assert.DoesNotContain(new FileGalleryStore().Load(), e => e.Prompt == "__brak__");
    }
}
