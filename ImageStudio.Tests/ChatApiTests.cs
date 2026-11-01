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

        var reply = await ImageGenerationService.ChatAsync(
            http, "/v1/chat/completions", "model-x",
            [new ChatMessage("system", "bądź zwięzły"), new ChatMessage("user", "cześć")], default);

        Assert.Equal("odpowiedź", reply);
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

        var reply = await ImageGenerationService.ChatAsync(http, "/v1/chat/completions", "m", [new ChatMessage("user", "x")], default);

        Assert.Equal("", reply);
    }

    [Fact]
    public async Task Chat_error_includes_status_and_api_message()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable, """{"error":{"code":"no_available_provider"}}""");
        using var http = Client(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ImageGenerationService.ChatAsync(http, "/v1/chat/completions", "m", [new ChatMessage("user", "x")], default));

        Assert.StartsWith("HTTP 503", error.Message);
        Assert.Contains("no_available_provider", error.Message);
    }

    // --- rozdzielanie promptu na system i koncept ---

    [Fact]
    public void ParseSplit_reads_plain_json()
    {
        var parts = ImageGenerationService.ParseSplit("""{"system":" styl ","concept":" kot "}""", "stary", "oryginał");

        Assert.Equal("styl", parts.System);
        Assert.Equal("kot", parts.Concept);
    }

    [Fact]
    public void ParseSplit_strips_code_fences()
    {
        var parts = ImageGenerationService.ParseSplit("```json\n{\"system\":\"s\",\"concept\":\"c\"}\n```", "stary", "oryginał");

        Assert.Equal("s", parts.System);
        Assert.Equal("c", parts.Concept);
    }

    [Fact]
    public void ParseSplit_falls_back_to_raw_reply_when_not_json()
    {
        var parts = ImageGenerationService.ParseSplit("  \"ładny zachód słońca\"  ", "stary system", "oryginał");

        Assert.Equal("stary system", parts.System);
        Assert.Equal("ładny zachód słońca", parts.Concept);
    }

    [Fact]
    public void ParseSplit_keeps_fallback_system_when_json_has_only_concept()
    {
        var parts = ImageGenerationService.ParseSplit("""{"concept":"tylko koncept"}""", "  stary system ", "oryginał");

        Assert.Equal("stary system", parts.System);
        Assert.Equal("tylko koncept", parts.Concept);
    }
}
