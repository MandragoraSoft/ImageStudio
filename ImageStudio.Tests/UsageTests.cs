using System.Net;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using ImageStudio.Views;

namespace ImageStudio.Tests;

// Zużycie tokenów pod odpowiedzią czatu: wejście, wyjście i z cache
public sealed class UsageTests
{
    private static async Task<ChatReply> Chat(string usageJson)
    {
        var body = "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]" + (usageJson.Length > 0 ? ",\"usage\":" + usageJson : "") + "}";
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, body)) { BaseAddress = new Uri("https://example.test") };
        return await OpenAiCompat.ChatAsync(http, "/v1/chat/completions", "m", [new ChatMessage("user", "x")], null, default);
    }

    [Theory]
    [InlineData("{\"prompt_tokens\":1234,\"completion_tokens\":210,\"prompt_tokens_details\":{\"cached_tokens\":1024}}", 1234, 210, 1024)]   // OpenAI, OpenRouter
    [InlineData("{\"prompt_tokens\":50,\"completion_tokens\":5,\"prompt_cache_hit_tokens\":40}", 50, 5, 40)]                                // DeepSeek
    [InlineData("{\"input_tokens\":70,\"output_tokens\":7,\"cache_read_input_tokens\":60}", 70, 7, 60)]                                      // styl Anthropic
    [InlineData("{\"prompt_tokens\":10,\"completion_tokens\":2}", 10, 2, null)]                                                             // bez informacji o cache
    public async Task Usage_is_read_from_the_common_response_formats(string usage, int input, int output, int? cached)
    {
        var reply = await Chat(usage);

        Assert.Equal(new TokenUsage(input, output, cached), reply.Usage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task Missing_or_empty_usage_gives_null(string usage)
    {
        Assert.Null((await Chat(usage)).Usage);
    }

    [Fact]
    public void Usage_text_shows_in_out_and_cache_with_dashes_for_missing_values()
    {
        Assert.Equal($"Tokeny: wejście {1234:N0} · wyjście {210:N0} · cache {1024:N0}",
                     new ChatMessage("assistant", "x", Usage: new TokenUsage(1234, 210, 1024)).UsageText);
        Assert.Contains("cache —", new ChatMessage("assistant", "x", Usage: new TokenUsage(10, 2, null)).UsageText);
        Assert.False(new ChatMessage("assistant", "x").HasUsage);
        Assert.Equal("", new ChatMessage("assistant", "x").UsageText);
    }

    [Fact]
    public void Chat_keeps_the_usage_of_every_assistant_message_including_tool_rounds()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            app.Provider.OnReply = history => history.Any(m => m.Role == "tool")
                ? new ChatReply("gotowe", [], Usage: new TokenUsage(200, 20, 150))
                : new ChatReply("", [new ToolCall("c1", "fs__read", "{}")], Usage: new TokenUsage(100, 10, 0));
            var host = new FakeToolHost();
            var withTools = new ChatViewModel(() => app.Provider, ["m1"], (_, _) => { }, "", host, modelInfo: id => new ChatModelInfo(id, []) { SupportsTools = true }) { SelectedModel = "m1", Input = "czytaj" };

            withTools.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            var assistants = withTools.Messages.Where(m => m.Role == "assistant").ToList();
            Assert.Equal([new TokenUsage(100, 10, 0), new TokenUsage(200, 20, 150)], assistants.Select(m => m.Usage));
        });
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
    public void Conversation_shows_the_usage_line_only_under_messages_that_have_it()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var chat = app.Vm.Chat;
            chat.Messages.Add(new ChatMessage("user", "pytanie"));
            chat.Messages.Add(new ChatMessage("assistant", "odpowiedź", Usage: new TokenUsage(1234, 210, 1024)));
            var window = new Window { Content = new ChatConversation { DataContext = chat }, ShowInTaskbar = false };
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var lines = FindAll<System.Windows.Controls.TextBlock>(window).Where(t => t.Text.StartsWith("Tokeny:")).ToList();

            Assert.Single(lines);
            Assert.Contains("cache", lines[0].Text);
            Assert.True(lines[0].IsVisible);
            window.Close();
        });
    }
}
