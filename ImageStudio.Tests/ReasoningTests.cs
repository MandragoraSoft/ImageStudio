using System.Net;
using System.Text.Json;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ImageStudio.Tests;

public sealed class ReasoningTests
{
    private static HttpClient Client(StubHandler handler) => new(handler) { BaseAddress = new Uri("https://example.test") };

    private static string Reply(string message) => "{\"choices\":[{\"message\":" + message + "}]}";

    private static async Task<(ChatReply Reply, JsonElement Body)> Chat(ChatOptions? options, string message = "{\"content\":\"ok\"}")
    {
        var handler = new StubHandler(HttpStatusCode.OK, Reply(message));
        using var http = Client(handler);
        var reply = await OpenAiCompat.ChatAsync(http, "/v1/chat/completions", "m", [new ChatMessage("user", "x")], null, default, options);
        return (reply, JsonDocument.Parse(handler.RequestBody!).RootElement.Clone());
    }

    [Fact]
    public void Parameter_is_chosen_from_the_models_supported_parameters()
    {
        Assert.Equal("reasoning_effort", ReasoningSupport.ParameterFor(["temperature", "reasoning", "reasoning_effort"]));
        Assert.Equal("reasoning", ReasoningSupport.ParameterFor(["include_reasoning", "reasoning"]));
        Assert.Equal("include_reasoning", ReasoningSupport.ParameterFor(["include_reasoning"]));
        Assert.Null(ReasoningSupport.ParameterFor(["temperature", "top_p"]));
    }

    [Fact]
    public async Task Request_without_options_has_no_reasoning_fields()
    {
        var (_, body) = await Chat(null);

        Assert.False(body.TryGetProperty("reasoning", out _));
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
        Assert.False(body.TryGetProperty("include_reasoning", out _));
    }

    [Fact]
    public async Task Reasoning_effort_style_sends_the_level_only_when_enabled()
    {
        var (_, on) = await Chat(new ChatOptions("reasoning_effort", true, "high"));
        Assert.Equal("high", on.GetProperty("reasoning_effort").GetString());

        var (_, defaulted) = await Chat(new ChatOptions("reasoning_effort", true));
        Assert.Equal("medium", defaulted.GetProperty("reasoning_effort").GetString());

        var (_, off) = await Chat(new ChatOptions("reasoning_effort", false, "high"));
        Assert.False(off.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task Reasoning_object_style_sends_effort_or_disables()
    {
        var (_, on) = await Chat(new ChatOptions("reasoning", true, "low"));
        Assert.Equal("low", on.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(on.GetProperty("reasoning").GetProperty("exclude").GetBoolean());

        var (_, off) = await Chat(new ChatOptions("reasoning", false));
        Assert.False(off.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Include_reasoning_style_toggles_the_flag()
    {
        var (_, on) = await Chat(new ChatOptions("include_reasoning", true));
        Assert.True(on.GetProperty("include_reasoning").GetBoolean());

        var (_, off) = await Chat(new ChatOptions("include_reasoning", false));
        Assert.False(off.GetProperty("include_reasoning").GetBoolean());
    }

    [Theory]
    [InlineData("{\"content\":\"o\",\"reasoning_content\":\"myślę A\"}", "myślę A")]
    [InlineData("{\"content\":\"o\",\"reasoning\":\"myślę B\"}", "myślę B")]
    [InlineData("{\"content\":\"o\",\"reasoning_details\":[{\"text\":\"krok 1\"},{\"summary\":\"krok 2\"},{\"type\":\"x\"}]}", "krok 1\nkrok 2")]
    public async Task Reply_carries_the_visible_reasoning_when_the_api_returns_it(string message, string expected)
    {
        var (reply, _) = await Chat(null, message);

        Assert.Equal("o", reply.Content);
        Assert.Equal(expected, reply.Reasoning);
    }

    [Theory]
    [InlineData("{\"content\":\"o\"}")]
    [InlineData("{\"content\":\"o\",\"reasoning\":\"  \"}")]
    [InlineData("{\"content\":\"o\",\"reasoning_details\":[]}")]
    public async Task Reply_without_reasoning_has_null(string message)
    {
        var (reply, _) = await Chat(null, message);

        Assert.Null(reply.Reasoning);
    }

    [Fact]
    public async Task Reasoning_is_never_sent_back_in_the_history()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Reply("{\"content\":\"ok\"}"));
        using var http = Client(handler);
        List<ChatMessage> history = [new("user", "pytanie"), new("assistant", "odpowiedź", Reasoning: "tajne rozumowanie")];

        await OpenAiCompat.ChatAsync(http, "/v1/chat/completions", "m", history, null, default);

        Assert.DoesNotContain("tajne rozumowanie", handler.RequestBody!);
    }

    // ---- ChatViewModel ----

    private static (TestApp App, ChatViewModel Chat) ChatWith(params string[] supported)
    {
        var app = TestApp.Create();
        var vm = app.Vm;
        vm.EnhancerModels.Add("m1");
        app.Provider.ChatCatalog = [new ChatModelInfo("m1", supported)];
        vm.ReloadModelsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        vm.Chat.SelectedModel = "m1";
        return (app, vm.Chat);
    }

    [Fact]
    public void Reasoning_controls_appear_only_for_models_that_declare_them()
    {
        Sta.Run(() =>
        {
            var (app, chat) = ChatWith("temperature", "reasoning_effort");
            using var _ = app;
            Assert.True(chat.SupportsReasoning);

            app.Provider.ChatCatalog = [new ChatModelInfo("m1", ["temperature"])];
            app.Vm.ReloadModelsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.False(chat.SupportsReasoning);
        });
    }

    [Fact]
    public void Send_passes_the_chosen_reasoning_options_and_shows_returned_reasoning()
    {
        Sta.Run(() =>
        {
            var (app, chat) = ChatWith("reasoning");
            using var _ = app;
            app.Provider.OnReply = _ => new ChatReply("odpowiedź", [], "najpierw pomyślałem");
            chat.SelectedReasoningEffort = "high";
            chat.Input = "pytanie";

            chat.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Equal(new ChatOptions("reasoning", true, "high"), app.Provider.LastOptions);
            var answer = chat.Messages.Last();
            Assert.Equal("najpierw pomyślałem", answer.Reasoning);
            Assert.True(answer.HasReasoning);
        });
    }

    [Fact]
    public void Disabled_reasoning_is_passed_as_disabled_and_unsupported_models_get_no_options()
    {
        Sta.Run(() =>
        {
            var (app, chat) = ChatWith("include_reasoning");
            using var _ = app;
            app.Provider.OnReply = _ => new ChatReply("ok", []);
            chat.ReasoningEnabled = false;
            chat.Input = "a";
            chat.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.False(app.Provider.LastOptions!.Enabled);

            app.Provider.ChatCatalog = [new ChatModelInfo("m1", ["temperature"])];
            app.Vm.ReloadModelsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            chat.Input = "b";
            chat.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Null(app.Provider.LastOptions);
        });
    }

    [Fact]
    public void Reasoning_choice_is_saved_and_restored_through_the_state_store()
    {
        Sta.Run(() =>
        {
            var state = new MemoryUserStateStore();
            using (var first = TestApp.Create(s => s.AddSingleton<IUserStateStore>(state)))
            {
                var vm = first.Vm;
                vm.InitializeAsync().GetAwaiter().GetResult();
                vm.Chat.ReasoningEnabled = false;
                vm.Chat.SelectedReasoningEffort = "low";
            }

            using var second = TestApp.Create(s => s.AddSingleton<IUserStateStore>(state));

            Assert.False(second.Vm.Chat.ReasoningEnabled);
            Assert.Equal("low", second.Vm.Chat.SelectedReasoningEffort);
        });
    }
}
