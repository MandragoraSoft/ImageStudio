using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

public sealed class ReloadTests
{
    private static ImageModelInfo Info(string id) => new(id, "owner", false, ["text"]);

    [Fact]
    public async Task Default_reload_collects_parts_and_reports_failures_as_warnings()
    {
        IAiProvider provider = new FakeProvider
        {
            ImageCatalog = new ImageModels([Info("a")], [Info("b")]),
            ChatCatalog = [new ChatModelInfo("chat", [])],
        };

        var catalog = await provider.ReloadAsync();

        Assert.Equal("a", catalog.Image!.Generation.Single().Id);
        Assert.Equal("chat", catalog.Chat!.Single().Id);
        Assert.Null(catalog.Video);
        Assert.Empty(catalog.Pricing);
        Assert.Equal(2, catalog.Warnings.Count);  // wideo i ceny
        Assert.Contains(catalog.Warnings, w => w.StartsWith("modele wideo"));
    }

    [Fact]
    public void Reload_command_refetches_models_and_keeps_the_selection()
    {
        Sta.Run(() =>
        {
            var provider = new FakeProvider
            {
                ImageCatalog = new ImageModels([Info("a"), Info("b")], [Info("e")]),
                VideoCatalog = [Info("v1"), Info("v2")],
                ChatCatalog = [new ChatModelInfo("chat", [])],
            };
            var vm = TestApp.CreateVm(provider: provider);
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.SelectedModel = vm.Models.Single(m => m.Id == "b");
            vm.SelectedVideoModel = vm.VideoModels.Single(m => m.Id == "v2");
            Assert.Equal(1, provider.ImageCalls);

            // dostawca dodał model; odświeżenie z GUI ma go pokazać bez gubienia wyboru
            provider.ImageCatalog = new ImageModels([Info("a"), Info("b"), Info("c")], [Info("e")]);
            Assert.True(vm.ReloadModelsCommand.CanExecute(null));
            vm.ReloadModelsCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Equal(2, provider.ImageCalls);
            Assert.Equal(3, vm.Models.Count);
            Assert.Equal("b", vm.SelectedModel!.Id);
            Assert.Equal("v2", vm.SelectedVideoModel!.Id);
            Assert.Contains("3 generowania", vm.Status);
        });
    }

    [Fact]
    public void Failed_part_keeps_the_previous_list_and_is_reported_in_status()
    {
        Sta.Run(() =>
        {
            var provider = new FakeProvider { ImageCatalog = new ImageModels([Info("a")], [Info("e")]) };
            var vm = TestApp.CreateVm(provider: provider);
            vm.InitializeAsync().GetAwaiter().GetResult();

            provider.ImageCatalog = null;  // następne pobranie modeli obrazów kończy się błędem
            vm.ReloadModelsCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Single(vm.Models);
            Assert.Contains("Uwagi", vm.Status);
            Assert.Contains("modele obrazów", vm.Status);
        });
    }

    private static SettingsResult Draft(string provider = "openrouter") => new("u", "m", 1536, "", "system", provider);

    [Fact]
    public async Task Settings_reload_fetches_text_models_with_the_typed_values_and_keeps_the_selection()
    {
        SettingsResult? seen = null;
        var vm = new ProviderSettingsTab(Draft(), draft =>
        {
            seen = draft;
            return Task.FromResult<IReadOnlyList<string>>(["openai/gpt-4o-mini", "anthropic/claude", "x/y"]);
        });
        vm.OpenRouter.ApiKey = " sk-or-nowy ";
        vm.OpenRouter.DefaultTextModel = "x/y";

        await vm.ReloadTextModelsCommand.ExecuteAsync(null);

        Assert.Equal("sk-or-nowy", seen!.OpenRouterKey);
        Assert.Equal("openrouter", seen.ProviderId);
        Assert.Equal(3, vm.OpenRouter.TextModels.Count);
        Assert.Equal("x/y", vm.OpenRouter.DefaultTextModel);
        Assert.Equal("Pobrano modeli: 3", vm.TextModelsStatus);
    }

    [Fact]
    public async Task Settings_reload_reports_errors_and_keeps_the_list()
    {
        var vm = new ProviderSettingsTab(Draft(), _ => throw new InvalidOperationException("401"));
        var before = vm.OpenRouter.TextModels.ToList();

        await vm.ReloadTextModelsCommand.ExecuteAsync(null);

        Assert.Equal(before, vm.OpenRouter.TextModels);
        Assert.Contains("401", vm.TextModelsStatus);
    }

    [Fact]
    public void Settings_reload_is_disabled_without_a_loader()
    {
        Assert.False(new ProviderSettingsTab(Draft()).ReloadTextModelsCommand.CanExecute(null));
    }
}
