using System.Net;
using System.Text.Json;
using ImageStudio.Services;

namespace ImageStudio.Tests;

public class ChatApiTests
{
    private static HttpClient Client(StubHandler handler) => new(handler) { BaseAddress = new Uri("https://example.test") };

    [Fact]
    public async Task Chat_posts_model_and_history_and_returns_the_reply()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"odpowiedź"}}]}""");
        using var http = Client(handler);

        var reply = await OpenAiCompat.ChatAsync(
            http, "/v1/chat/completions", "model-x",
            [new ChatMessage("system", "bądź zwięzły"), new ChatMessage("user", "cześć")], null, default);

        Assert.Equal("odpowiedź", reply.Content);
        Assert.Empty(reply.ToolCalls);
        Assert.Equal("/v1/chat/completions", handler.RequestPath);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("model-x", body.RootElement.GetProperty("model").GetString());
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("cześć", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Chat_returns_empty_text_when_content_is_null()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"choices":[{"message":{"content":null}}]}""");
        using var http = Client(handler);

        var reply = await OpenAiCompat.ChatAsync(http, "/v1/chat/completions", "m", [new ChatMessage("user", "x")], null, default);

        Assert.Equal("", reply.Content);
    }

    [Fact]
    public async Task Chat_error_includes_status_and_api_message()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable, """{"error":{"code":"no_available_provider"}}""");
        using var http = Client(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OpenAiCompat.ChatAsync(http, "/v1/chat/completions", "m", [new ChatMessage("user", "x")], null, default));

        Assert.StartsWith("HTTP 503", error.Message);
        Assert.Contains("no_available_provider", error.Message);
    }

    // --- rozdzielanie promptu na system i koncept ---

    [Fact]
    public void ParseSplit_reads_plain_json()
    {
        var parts = OpenAiCompat.ParseSplit("""{"system":" styl ","concept":" kot ","negative":" tekst "}""");

        Assert.Equal("styl", parts.System);
        Assert.Equal("kot", parts.Concept);
        Assert.Equal("tekst", parts.Negative);
    }

    [Theory]
    [InlineData("nie JSON")]
    [InlineData("\"tekst\"")]
    [InlineData("[]")]
    [InlineData("```json\n{\"system\":\"s\",\"concept\":\"c\",\"negative\":\"n\"}\n```")]
    [InlineData("""{"concept":"c","negative":"n"}""")]
    [InlineData("""{"system":"s","negative":"n"}""")]
    [InlineData("""{"system":"s","concept":"c"}""")]
    [InlineData("""{"system":null,"concept":"c","negative":"n"}""")]
    [InlineData("""{"system":"s","concept":2,"negative":"n"}""")]
    [InlineData("""{"system":"s","concept":"c","negative":[]}""")]
    [InlineData("""{"system":"","concept":"   ","negative":""}""")]
    public void ParseSplit_rejects_invalid_or_incomplete_responses(string reply)
    {
        Assert.Throws<InvalidOperationException>(() => OpenAiCompat.ParseSplit(reply));
    }

    [Fact]
    public void ParseSplit_accepts_empty_system_and_negative()
    {
        Assert.Equal(new PromptParts("", "kot", null, ""),
                     OpenAiCompat.ParseSplit("""{"system":"","concept":"kot","negative":""}"""));
    }
}
