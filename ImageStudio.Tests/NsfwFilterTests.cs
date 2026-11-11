using CommunityToolkit.Mvvm.Input;
using ImageStudio.Configuration;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

// Filtr NSFW jest trójstanowy (tylko, wszystkie, ukryj) w obu miejscach: pasku filtrów i przeglądarce modeli
public sealed class NsfwFilterTests
{
    private static ModelOption Opt(string id, bool nsfw) => new(id, "owner", nsfw, ["text"], null);

    private static ModelFilter Filter() => new(true, () => new ProviderModelPrefs(), new RelayCommand(() => { }));

    private static List<string> Visible(ModelFilter filter, params ModelOption[] models) =>
        models.Where(filter.Matches).Select(m => m.Id).ToList();

    private static ModelBrowserViewModel Browser(params ModelOption[] models)
    {
        var store = new ModelPreferences { PathOverride = Path.Combine(Path.GetTempPath(), "nsfw-" + Guid.NewGuid().ToString("N") + ".json") };
        return new ModelBrowserViewModel("Prov", models.Select(m => (m, "generowanie")), store.For("prov"), store);
    }

    private static List<string> BrowserVisible(ModelBrowserViewModel vm) =>
        vm.View.Cast<ModelBrowserItem>().Select(i => i.Option.Id).ToList();

    private static readonly ModelOption Safe = Opt("safe", false);
    private static readonly ModelOption Adult = Opt("adult", true);

    [Fact]
    public void Any_shows_all_models()
    {
        var filter = Filter();
        Assert.Equal(TriState.Any, filter.Nsfw);
        Assert.Equal(["safe", "adult"], Visible(filter, Safe, Adult));
    }

    [Fact]
    public void Exclude_hides_nsfw_models()
    {
        var filter = Filter();
        filter.Nsfw = TriState.Exclude;
        Assert.Equal(["safe"], Visible(filter, Safe, Adult));
    }

    [Fact]
    public void Only_shows_only_nsfw_models()
    {
        var filter = Filter();
        filter.Nsfw = TriState.Only;
        Assert.Equal(["adult"], Visible(filter, Safe, Adult));
    }

    [Fact]
    public void Changing_nsfw_raises_changed()
    {
        var filter = Filter();
        var changes = 0;
        filter.Changed += () => changes++;

        filter.Nsfw = TriState.Only;
        filter.Nsfw = TriState.Exclude;

        Assert.Equal(2, changes);
    }

    [Fact]
    public void Nsfw_combines_with_search()
    {
        var filter = Filter();
        filter.Nsfw = TriState.Only;
        filter.SearchText = "safe";

        Assert.Empty(Visible(filter, Safe, Adult));
    }

    [Fact]
    public void Reset_keeps_nsfw_choice()
    {
        var filter = Filter();
        filter.Nsfw = TriState.Exclude;
        filter.Reset();

        Assert.Equal(TriState.Exclude, filter.Nsfw);
    }

    [Fact]
    public void Browser_any_shows_all_models()
    {
        var vm = Browser(Safe, Adult);
        Assert.Equal(TriState.Any, vm.Nsfw);
        Assert.Equal(["adult", "safe"], BrowserVisible(vm));
    }

    [Fact]
    public void Browser_exclude_hides_nsfw_models()
    {
        var vm = Browser(Safe, Adult);
        vm.Nsfw = TriState.Exclude;
        Assert.Equal(["safe"], BrowserVisible(vm));
    }

    [Fact]
    public void Browser_only_shows_only_nsfw_models()
    {
        var vm = Browser(Safe, Adult);
        vm.Nsfw = TriState.Only;
        Assert.Equal(["adult"], BrowserVisible(vm));
    }
}
