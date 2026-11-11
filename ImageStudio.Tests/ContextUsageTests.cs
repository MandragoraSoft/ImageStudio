using System.Text.Json;
using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

// Szacowanie tokenów, odczyt rozmiaru okna z katalogu i wskaźnik zużycia kontekstu w czacie
public sealed class ContextUsageTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    public void Estimate_is_about_one_token_per_four_characters(string? text, int expected)
    {
        Assert.Equal(expected, TokenEstimator.Estimate(text));
    }

    [Theory]
    [InlineData(950, "950")]
    [InlineData(54321, "54k")]
    [InlineData(128000, "128k")]
    [InlineData(1_250_000, "1.3M")]
    public void Short_formats_counts_for_the_indicator(int tokens, string expected)
    {
        Assert.Equal(expected, TokenEstimator.Short(tokens));
    }

    [Fact]
    public void Estimate_of_a_tool_sums_name_description_and_schema()
    {
        var tool = new ToolDefinition("ab", "abcd", "{}");

        Assert.Equal(TokenEstimator.Estimate("ab") + TokenEstimator.Estimate("abcd") + TokenEstimator.Estimate("{}"),
                     TokenEstimator.Estimate(tool));
    }

    [Theory]
    [InlineData("{\"context_length\":128000}", 128000)]
    [InlineData("{\"max_context_length\":32000}", 32000)]
    [InlineData("{\"top_provider\":{\"context_length\":8000}}", 8000)]
    [InlineData("{\"context_length\":0}", null)]
    [InlineData("{\"context_length\":\"abc\"}", null)]
    [InlineData("{}", null)]
    public void ReadContextLength_takes_the_first_positive_number_from_known_fields(string json, int? expected)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(expected, OpenAiCompat.ReadContextLength(doc.RootElement));
    }

    [Fact]
    public async Task Usage_from_last_reply_sets_context()
    {
        var (vm, provider) = Create(contextLength: 4000);
        provider.OnReply = _ => new ChatReply("ok", [], Usage: new TokenUsage(1000, 200, null));

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(1200, vm.ContextUsed);
        Assert.False(vm.ContextEstimated);
        Assert.Equal(30, vm.ContextPercent);
        Assert.Equal("30% · 1k / 4k", vm.ContextText);
        Assert.Equal("normal", vm.ContextLevel);
    }

    [Fact]
    public async Task Unknown_limit_shows_tokens_only()
    {
        var (vm, provider) = Create(contextLength: null);
        provider.OnReply = _ => new ChatReply("ok", [], Usage: new TokenUsage(1000, 200, null));

        await vm.SendCommand.ExecuteAsync(null);

        Assert.False(vm.HasContextLimit);
        Assert.Equal(0, vm.ContextPercent);
        Assert.Contains("1k", vm.ContextText);
        Assert.DoesNotContain("%", vm.ContextText);
    }

    [Fact]
    public async Task No_usage_falls_back_to_estimate()
    {
        var (vm, provider) = Create(contextLength: 4000);
        provider.OnReply = _ => new ChatReply("ok", []);

        await vm.SendCommand.ExecuteAsync(null);

        Assert.True(vm.ContextEstimated);
        Assert.True(vm.ContextUsed > 0);
        Assert.Contains("~", vm.ContextText);
    }

    [Fact]
    public async Task Over_limit_caps_bar_not_text()
    {
        var (vm, provider) = Create(contextLength: 4000);
        provider.OnReply = _ => new ChatReply("ok", [], Usage: new TokenUsage(5000, 0, null));

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(100, vm.ContextBarValue);
        Assert.Equal(125, vm.ContextPercent);
        Assert.Equal("critical", vm.ContextLevel);
        Assert.Contains("125%", vm.ContextText);
    }

    [Theory]
    [InlineData(2960, "normal")]
    [InlineData(3000, "warn")]
    [InlineData(3600, "critical")]
    public async Task Levels_change_at_75_and_90_percent(int input, string level)
    {
        var (vm, provider) = Create(contextLength: 4000);
        provider.OnReply = _ => new ChatReply("ok", [], Usage: new TokenUsage(input, 0, null));

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(level, vm.ContextLevel);
    }

    [Fact]
    public async Task Empty_chat_hides_indicator_and_new_session_resets_it()
    {
        var (vm, provider) = Create(contextLength: 4000);
        Assert.False(vm.HasContext);
        provider.OnReply = _ => new ChatReply("ok", [], Usage: new TokenUsage(1000, 200, null));
        await vm.SendCommand.ExecuteAsync(null);
        Assert.True(vm.HasContext);

        vm.NewChatCommand.Execute(null);

        Assert.False(vm.HasContext);
    }

    private static (ChatViewModel Vm, FakeProvider Provider) Create(int? contextLength)
    {
        var provider = new FakeProvider();
        var vm = new ChatViewModel(() => provider, ["m1"], (_, _) => { }, "Bądź zwięzły",
                                   modelInfo: id => new ChatModelInfo(id, [], ContextLength: contextLength))
        {
            SelectedModel = "m1",
            Input = "cześć",
        };
        return (vm, provider);
    }
}
