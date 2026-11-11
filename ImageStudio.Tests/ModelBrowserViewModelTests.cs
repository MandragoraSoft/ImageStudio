using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

public sealed class ModelBrowserViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "imagestudio-tests-" + Guid.NewGuid().ToString("N"));
    private string PrefsPath => Path.Combine(_dir, "models.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static ModelOption Opt(string id, string? owner = "owner", bool nsfw = false, string[]? input = null, string? description = null) =>
        new(id, owner, nsfw, input ?? ["text"], null, description);

    private (ModelBrowserViewModel Vm, ProviderModelPrefs Prefs, ModelPreferences Store) Create(params (ModelOption, string)[] rows)
    {
        var store = new ModelPreferences { PathOverride = PrefsPath };
        var prefs = store.For("prov");
        return (new ModelBrowserViewModel("Prov", rows, prefs, store), prefs, store);
    }

    private static List<string> Visible(ModelBrowserViewModel vm) =>
        vm.View.Cast<ModelBrowserItem>().Select(i => i.Option.Id).ToList();

    [Fact]
    public void Same_model_in_several_kinds_is_one_row_with_all_kinds()
    {
        var (vm, _, _) = Create((Opt("a"), "generowanie"), (Opt("a"), "edycja"), (Opt("b"), "wideo"));

        Assert.Equal(["a", "b"], Visible(vm));
        Assert.Equal("generowanie, edycja", vm.Items.First(i => i.Option.Id == "a").Kinds);
    }

    [Fact]
    public void Search_matches_id_and_description()
    {
        var (vm, _, _) = Create((Opt("flux-3"), "generowanie"), (Opt("other", description: "Szybki model FLUX"), "generowanie"), (Opt("zzz"), "generowanie"));

        vm.SearchText = "flux";

        Assert.Equal(["flux-3", "other"], Visible(vm));
    }

    [Fact]
    public void Kind_filter_keeps_models_that_have_that_kind()
    {
        var (vm, _, _) = Create((Opt("a"), "generowanie"), (Opt("a"), "edycja"), (Opt("v"), "wideo"));

        vm.SelectedKind = "edycja";
        Assert.Equal(["a"], Visible(vm));

        vm.SelectedKind = "wideo";
        Assert.Equal(["v"], Visible(vm));

        vm.SelectedKind = ModelBrowserViewModel.All;
        Assert.Equal(["a", "v"], Visible(vm));
    }

    [Fact]
    public void Owner_filter_and_owner_list()
    {
        var (vm, _, _) = Create((Opt("a", owner: "google"), "generowanie"), (Opt("b", owner: "recraft"), "generowanie"),
                                (Opt("c", owner: null), "generowanie"));

        Assert.Equal([ModelBrowserViewModel.All, "google", "recraft"], vm.Owners);

        vm.SelectedOwner = "recraft";
        Assert.Equal(["b"], Visible(vm));
    }

    [Fact]
    public void Image_input_and_nsfw_filters()
    {
        var (vm, _, _) = Create((Opt("text-only"), "generowanie"),
                                (Opt("with-image", input: ["text", "image"]), "generowanie"),
                                (Opt("adult", nsfw: true, input: ["text", "image"]), "generowanie"));

        vm.AcceptsImageOnly = true;
        Assert.Equal(["adult", "with-image"], Visible(vm));

        vm.Nsfw = TriState.Exclude;
        Assert.Equal(["with-image"], Visible(vm));
    }

    [Fact]
    public void Preferred_flag_comes_from_prefs_and_only_preferred_filters()
    {
        var store = new ModelPreferences { PathOverride = PrefsPath };
        var prefs = store.For("prov");
        prefs.Preferred.Add("b");
        var vm = new ModelBrowserViewModel("Prov", [(Opt("a"), "generowanie"), (Opt("b"), "generowanie")], prefs, store);

        Assert.True(vm.Items.First(i => i.Option.Id == "b").IsPreferred);

        vm.OnlyPreferred = true;
        Assert.Equal(["b"], Visible(vm));
        Assert.Equal("Preferowane: 1 z 2", vm.Summary);
    }

    [Fact]
    public void Toggling_star_updates_prefs_and_saves_the_file()
    {
        var (vm, prefs, _) = Create((Opt("a"), "generowanie"), (Opt("b"), "generowanie"));

        vm.Items.First(i => i.Option.Id == "a").IsPreferred = true;

        Assert.Contains("a", prefs.Preferred);
        Assert.Equal("Preferowane: 1 z 2", vm.Summary);
        var reloaded = ModelPreferences.Load(PrefsPath);
        Assert.Contains("a", reloaded.For("prov").Preferred);

        vm.Items.First(i => i.Option.Id == "a").IsPreferred = false;

        Assert.DoesNotContain("a", prefs.Preferred);
        Assert.DoesNotContain("a", ModelPreferences.Load(PrefsPath).For("prov").Preferred);
    }

    [Fact]
    public void Description_falls_back_to_attributes_when_api_has_none()
    {
        var withText = new ModelBrowserItem(Opt("a", description: "Opis z API"), ["generowanie"], false);
        var without = new ModelBrowserItem(Opt("b", owner: "google", nsfw: true, input: ["text", "image"]), ["generowanie"], false);

        Assert.Equal("Opis z API", withText.Description);
        Assert.Contains("google", without.Description);
        Assert.Contains("text, image", without.Description);
        Assert.Contains("NSFW", without.Description);
    }
}
