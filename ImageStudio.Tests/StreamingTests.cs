using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using ImageStudio.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ImageStudio.Tests;

// Odpowiedzi strumieniowe (SSE): parsowanie, wiadomość rosnąca w czacie i ustawienie włączające strumień
public sealed class StreamingTests
{
    private static string Sse(params string[] chunks) =>
        string.Join("\n\n", chunks.Select(c => c == "[DONE]" || c.StartsWith(':') ? c : "data: " + c)) + "\n\n";

    private static string Chunk(string delta, string? extra = null) =>
        "{\"choices\":[{\"index\":0,\"delta\":" + delta + "}]" + (extra is null ? "" : "," + extra) + "}";

    private static async Task<(ChatReply Reply, List<ChatDelta> Deltas, JsonElement Body)> Stream(string sse, ChatOptions? options = null)
    {
        var handler = new StubHandler(HttpStatusCode.OK, sse);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var deltas = new List<ChatDelta>();
        var reply = await OpenAiCompat.ChatStreamAsync(http, "/v1/chat/completions", "m", [new ChatMessage("user", "x")], null,
                                                       deltas.Add, default, options);
        return (reply, deltas, JsonDocument.Parse(handler.RequestBody!).RootElement.Clone());
    }

    [Fact]
    public async Task Request_asks_for_a_stream_with_usage()
    {
        var (_, _, body) = await Stream(Sse("[DONE]"));

        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal("x", body.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Text_chunks_are_delivered_in_order_and_joined_keeping_whitespace()
    {
        var sse = Sse(": ping",
                      Chunk("{\"role\":\"assistant\",\"content\":\"\"}"),
                      Chunk("{\"content\":\"Ala\"}"),
                      Chunk("{\"content\":\" \"}"),
                      Chunk("{\"content\":\"ma kota\"}"),
                      "{\"choices\":[],\"usage\":{\"prompt_tokens\":25,\"completion_tokens\":14,\"prompt_tokens_details\":{\"cached_tokens\":12}}}",
                      "[DONE]");

        var (reply, deltas, _) = await Stream(sse);

        Assert.Equal(["Ala", " ", "ma kota"], deltas.Select(d => d.Content));
        Assert.Equal("Ala ma kota", reply.Content);
        Assert.Equal(new TokenUsage(25, 14, 12), reply.Usage);
        Assert.Empty(reply.ToolCalls);
        Assert.Null(reply.Reasoning);
    }

    [Fact]
    public async Task Reasoning_chunks_are_delivered_and_collected()
    {
        var sse = Sse(Chunk("{\"reasoning_content\":\"Najpierw \"}"),
                      Chunk("{\"reasoning\":\"policzę.\"}"),
                      Chunk("{\"content\":\"42\"}"),
                      "[DONE]");

        var (reply, deltas, _) = await Stream(sse);

        Assert.Equal("Najpierw policzę.", reply.Reasoning);
        Assert.Equal("42", reply.Content);
        Assert.Equal(["Najpierw ", "policzę.", null], deltas.Select(d => d.Reasoning));
    }

    [Fact]
    public async Task Tool_call_fragments_are_assembled_by_index()
    {
        var sse = Sse(Chunk("{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"fs__read\",\"arguments\":\"\"}}]}"),
                      Chunk("{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"path\\\":\"}}]}"),
                      Chunk("{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"a.txt\\\"}\"}}]}"),
                      Chunk("{\"tool_calls\":[{\"index\":1,\"id\":\"c2\",\"function\":{\"name\":\"fs__list\"}}]}"),
                      "[DONE]");

        var (reply, deltas, _) = await Stream(sse);

        Assert.Empty(deltas);  // wywołania narzędzi nie są tekstem do pokazania
        Assert.Equal(2, reply.ToolCalls.Count);
        Assert.Equal(new ToolCall("c1", "fs__read", "{\"path\":\"a.txt\"}"), reply.ToolCalls[0]);
        Assert.Equal("fs__list", reply.ToolCalls[1].Name);
        Assert.Equal("{}", reply.ToolCalls[1].ArgumentsJson);
    }

    [Fact]
    public async Task Reasoning_options_are_sent_for_streams_too()
    {
        var (_, _, body) = await Stream(Sse("[DONE]"), new ChatOptions("reasoning_effort", true, "high"));

        Assert.Equal("high", body.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task Http_error_and_error_event_are_thrown()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.Unauthorized, "{\"error\":\"zły klucz\"}"))
        {
            BaseAddress = new Uri("https://example.test"),
        };
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OpenAiCompat.ChatStreamAsync(http, "/v1/chat/completions", "m", [new ChatMessage("user", "x")], null, _ => { }));
        Assert.Contains("HTTP 401", failed.Message);

        var inStream = await Assert.ThrowsAsync<InvalidOperationException>(() => Stream(Sse("{\"error\":{\"message\":\"limit\"}}")));
        Assert.Contains("limit", inStream.Message);
    }

    [Fact]
    public async Task Provider_without_a_stream_falls_back_to_one_full_delta()
    {
        IAiProvider provider = new FakeProvider { OnChat = (_, _) => "cała odpowiedź" };
        var deltas = new List<ChatDelta>();

        var reply = await provider.ChatStreamAsync("m", [new ChatMessage("user", "x")], null, deltas.Add);

        Assert.Equal("cała odpowiedź", reply.Content);
        Assert.Equal([new ChatDelta("cała odpowiedź", null)], deltas);
    }

    // ---- czat ----

    private static ChatViewModel StreamingChat(TestApp app)
    {
        var chat = app.Vm.Chat;
        chat.SelectedModel = "m1";
        return chat;
    }

    [Fact]
    public void Message_appears_at_once_and_grows_with_every_chunk_then_gets_the_final_data()
    {
        Sta.Run(() =>
        {
            var provider = new StreamingFakeProvider();
            using var app = TestApp.Create(provider: provider);
            var chat = StreamingChat(app);
            var seen = new List<string>();
            provider.Deltas.Add(new ChatDelta("Cze", null));
            provider.Deltas.Add(new ChatDelta(null, "myślę"));
            provider.Deltas.Add(new ChatDelta("ść", null));
            provider.AfterDelta = _ => seen.Add($"{chat.Messages.Count}:{chat.Messages.Last().Content}|{chat.Messages.Last().Reasoning}");
            provider.OnReply = _ => new ChatReply("Cześć", [], "myślę", new TokenUsage(10, 2, 8));
            var updates = 0;
            chat.MessageUpdated += () => updates++;
            chat.Input = "hej";

            chat.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Equal(["2:Cze|", "2:Cze|myślę", "2:Cześć|myślę"], seen);  // użytkownik + jedna rosnąca odpowiedź
            var answer = chat.Messages.Last();
            Assert.Equal(2, chat.Messages.Count);
            Assert.Equal("Cześć", answer.Content);
            Assert.Equal("myślę", answer.Reasoning);
            Assert.Equal(new TokenUsage(10, 2, 8), answer.Usage);
            Assert.True(updates >= 4);  // trzy kawałki i zamknięcie
            Assert.Equal(1, provider.StreamCalls);
            Assert.Equal(0, provider.ChatCalls - provider.StreamCalls);
        });
    }

    [Fact]
    public void Streamed_history_does_not_contain_the_message_being_written()
    {
        Sta.Run(() =>
        {
            var provider = new StreamingFakeProvider();
            using var app = TestApp.Create(provider: provider);
            var chat = StreamingChat(app);
            provider.OnChat = (_, _) => "ok";
            chat.Input = "pierwsze";

            chat.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Equal(["user"], provider.LastMessages.Select(m => m.Role));
        });
    }

    [Fact]
    public void Setting_off_uses_the_plain_request()
    {
        Sta.Run(() =>
        {
            var provider = new StreamingFakeProvider();
            using var app = TestApp.Create(s => s.AddSingleton(new AppSettings("u", "m", 1536, "", "system", UseStreaming: false)), provider);
            var chat = StreamingChat(app);
            provider.OnChat = (_, _) => "ok";
            chat.Input = "pytanie";

            chat.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Equal(0, provider.StreamCalls);
            Assert.Equal("ok", chat.Messages.Last().Content);
        });
    }

    [Fact]
    public void Failing_stream_without_text_leaves_no_empty_message_and_restores_the_input()
    {
        Sta.Run(() =>
        {
            var provider = new StreamingFakeProvider { FailAfterDeltas = new InvalidOperationException("HTTP 500") };
            using var app = TestApp.Create(provider: provider);
            var chat = StreamingChat(app);
            chat.Input = "pytanie";

            chat.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Empty(chat.Messages);
            Assert.Equal("pytanie", chat.Input);
            Assert.Contains("HTTP 500", app.Vm.Status);
        });
    }

    [Fact]
    public void Failing_stream_keeps_the_text_that_already_arrived()
    {
        Sta.Run(() =>
        {
            var provider = new StreamingFakeProvider { FailAfterDeltas = new InvalidOperationException("połączenie przerwane") };
            provider.Deltas.Add(new ChatDelta("Połowa odpo", null));
            using var app = TestApp.Create(provider: provider);
            var chat = StreamingChat(app);
            chat.Input = "pytanie";

            chat.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Equal(["pytanie", "Połowa odpo"], chat.Messages.Select(m => m.Content));
            Assert.Equal("", chat.Input);
        });
    }

    [Fact]
    public void Tool_round_in_streaming_mode_keeps_one_message_per_model_turn()
    {
        Sta.Run(() =>
        {
            var provider = new StreamingFakeProvider();
            using var app = TestApp.Create(provider: provider);
            var chat = StreamingChat(app);
            var host = new FakeToolHost();
            var withTools = new ChatViewModel(() => provider, ["m1"], (_, _) => { }, "", host,
                                              modelInfo: id => new ChatModelInfo(id, []) { SupportsTools = true }, useStreaming: () => true) { SelectedModel = "m1", Input = "czytaj" };
            provider.OnReply = history => history.Any(m => m.Role == "tool")
                ? new ChatReply("gotowe", [], Usage: new TokenUsage(20, 2, 0))
                : new ChatReply("", [new ToolCall("c1", "fs__read", "{}")]);

            withTools.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Equal(["user", "assistant", "tool", "assistant"], withTools.Messages.Select(m => m.Role));
            Assert.True(withTools.Messages[1].HasToolCalls);
            Assert.Equal("gotowe", withTools.Messages[3].Content);
            _ = chat;
        });
    }

    // ---- ustawienie ----

    [Fact]
    public void Settings_tab_exposes_and_applies_the_streaming_switch()
    {
        var current = new SettingsResult("u", "m", 1536, "", "system", UseStreaming: true);
        var tab = new AppSettingsTab(current);
        Assert.True(tab.UseStreaming);

        tab.UseStreaming = false;

        Assert.False(tab.Apply(current).UseStreaming);
    }

    [Fact]
    public void Streaming_switch_is_saved_through_the_settings_store_without_reloading_the_provider()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            var factory = (FakeProviderFactory)app.Get<IProviderFactory>();
            app.Dialogs.SettingsAnswer = current => current with { UseStreaming = false };
            app.Vm.InitializeAsync().GetAwaiter().GetResult();

            app.Vm.OpenSettingsCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.False(app.Settings.Settings.UseStreaming);
            Assert.Empty(factory.Created);  // sama zmiana trybu odpowiedzi nie wymienia dostawcy
        });
    }

    [Fact]
    public void Streaming_setting_round_trips_through_the_settings_file()
    {
        AppSettings.Save("https://a", "https://m", 1536, "", "system", useStreaming: false);
        Assert.False(AppSettings.Load().UseStreaming);

        AppSettings.Save("https://a", "https://m", 1536, "", "system", useStreaming: true);
        Assert.True(AppSettings.Load().UseStreaming);
    }

    // ---- spinner i widok ----

    [Fact]
    public async Task Stream_is_reported_in_the_footer_but_does_not_dim_the_response_window()
    {
        var tracker = new ActivityTracker();
        IAiProvider provider = new ActivityAiProvider(new FakeProvider { OnChat = (_, _) => "x" }, tracker);
        var dimsDuringStream = true;
        var activeDuringStream = false;

        await provider.ChatStreamAsync("m", [new ChatMessage("user", "x")], null, _ =>
        {
            activeDuringStream = tracker.IsActive;
            dimsDuringStream = tracker.DimsResponse;
        });

        Assert.True(activeDuringStream);
        Assert.False(dimsDuringStream);

        using (tracker.Begin("Generowanie obrazu")) Assert.True(tracker.DimsResponse);
        Assert.False(tracker.DimsResponse);
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    [Fact]
    public void Conversation_view_follows_a_message_that_is_still_being_written()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var chat = app.Vm.Chat;
            var live = new ChatMessage("assistant", "");
            chat.Messages.Add(live);
            var window = new Window { Content = new ChatConversation { DataContext = chat }, ShowInTaskbar = false };
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            live.Content = "Ala ma ";
            live.Content += "kota";
            live.Reasoning = "tok";
            live.Usage = new TokenUsage(5, 3, 0);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            FindAll<Expander>(window).Single(e => (string)e.Header == "Rozumowanie").IsExpanded = true;  // treść zwiniętej sekcji powstaje po rozwinięciu
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var texts = FindAll<TextBox>(window).Select(t => t.Text).ToList();
            Assert.Contains("Ala ma kota", texts);
            Assert.Contains("tok", texts);
            Assert.Contains(FindAll<TextBlock>(window), t => t.Text.StartsWith("Tokeny:") && t.IsVisible);
            window.Close();
        });
    }
}
