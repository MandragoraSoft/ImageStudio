using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

public sealed class PresetNamingTests
{
    [Fact]
    public void ParseDraft_reads_json_also_in_code_fence()
    {
        var draft = PresetSaveViewModel.ParseDraft("```json\n{\"name\":\" Kot w kapeluszu \",\"description\":\"Portret kota\"}\n```");
        Assert.Equal("Kot w kapeluszu", draft.Name);
        Assert.Equal("Portret kota", draft.Description);
    }

    [Fact]
    public void ParseDraft_falls_back_to_first_line_for_plain_text()
    {
        Assert.Equal("Zachód słońca", PresetSaveViewModel.ParseDraft("\"Zachód słońca\"\nreszta").Name);
    }

    [Fact]
    public void ParseDraft_rejects_empty_name()
    {
        Assert.Throws<InvalidOperationException>(() => PresetSaveViewModel.ParseDraft("{\"description\":\"x\"}"));
    }

    [Fact]
    public async Task Suggest_fills_name_and_description_and_reports_errors()
    {
        var ok = new PresetSaveViewModel("stara", "prompt",
                                         () => Task.FromResult(new PresetDraft("Nowa", "Opis")));
        await ok.SuggestCommand.ExecuteAsync(null);
        Assert.Equal("Nowa", ok.Name);
        Assert.Equal("Opis", ok.Description);

        var failing = new PresetSaveViewModel("stara", "prompt", () => throw new InvalidOperationException("brak sieci"));
        await failing.SuggestCommand.ExecuteAsync(null);
        Assert.Equal("stara", failing.Name);
        Assert.Contains("brak sieci", failing.Status);
    }

    [Fact]
    public void Providers_declare_default_text_model()
    {
        Assert.Equal("venice/uncensored", new UberLlmProvider("http://x", "k", "http://x").DefaultTextModel);
        Assert.False(string.IsNullOrWhiteSpace(new OpenRouterProvider("k").DefaultTextModel));
        Assert.False(string.IsNullOrWhiteSpace(new OpenAiProvider("http://x", "k").DefaultTextModel));
    }
}

public sealed class ProviderDefaultsTests
{
    [Fact]
    public void Registry_exposes_provider_default_text_models()
    {
        Assert.All(ProviderRegistry.Available, p => Assert.False(string.IsNullOrWhiteSpace(p.DefaultTextModel)));
    }

    [Fact]
    public void Settings_carry_default_text_model_per_provider()
    {
        var current = new SettingsResult("u", "m", 1536, "", "system",
                                         DefaultTextModels: new Dictionary<string, string?> { ["openai"] = "gpt-4.1" },
                                         ActiveTextModels: ["venice/uncensored", "venice/other"]);
        var vm = new SettingsViewModel(current);
        Assert.Equal("gpt-4.1", vm.OpenAi.DefaultTextModel);
        Assert.Contains("gpt-4o-mini", vm.OpenAi.TextModels);
        Assert.Equal("gpt-4.1", vm.OpenAi.PreferredTextModelOrNull);

        // Brak własnego wyboru: selektor pokazuje model domyślny dostawcy, a zapis zostaje pusty
        Assert.Equal("venice/uncensored", vm.UberLlm.DefaultTextModel);
        Assert.Contains("venice/other", vm.UberLlm.TextModels);
        Assert.Null(vm.UberLlm.PreferredTextModelOrNull);
    }
}
