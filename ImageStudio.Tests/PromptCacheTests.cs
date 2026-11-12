using System.Net;
using System.Text;
using System.Text.Json;
using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

public sealed class PromptCacheTests
{
    private sealed class CaptureHandler(List<string> requests) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            requests.Add(body);
            using var json = JsonDocument.Parse(body);
            var streaming = json.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean();
            var response = streaming
                ? "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n"
                : """{"choices":[{"message":{"content":"ok"}}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
            };
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Official_providers_send_a_stable_session_key_in_both_chat_modes(bool router, bool streaming)
    {
        var requests = new List<string>();
        Func<HttpMessageHandler> factory = () => new CaptureHandler(requests);
        using IAiProvider provider = router
            ? new OpenRouterProvider("", "https://openrouter.ai", factory)
            : new OpenAiProvider("https://api.openai.com", "", factory);
        var store = new MemoryChatSessionStore();
        var chat = new ChatViewModel(() => provider, ["m"], (_, _) => { }, "", useStreaming: () => streaming, sessionStore: store)
        {
            SelectedModel = "m"
        };
        async Task Send(string text)
        {
            chat.Input = text;
            await chat.SendCommand.ExecuteAsync(null);
        }

        await Send("pierwsza");
        var saved = Assert.Single(chat.Sessions);
        await Send("druga");
        chat.NewIncognitoChatCommand.Execute(null);
        await Send("incognito");
        Assert.Single(store.Sessions);
        chat.SelectedSession = saved;
        await Send("wznowiona");
        chat.NewChatCommand.Execute(null);
        await Send("nowa");

        var field = router ? "session_id" : "prompt_cache_key";
        var keys = requests.Select(body =>
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            Assert.False(root.TryGetProperty(router ? "prompt_cache_key" : "session_id", out _));
            return root.GetProperty(field).GetString();
        }).ToArray();
        Assert.All(keys, key => Assert.True(Guid.TryParseExact(key, "N", out _)));
        Assert.Equal(keys[0], keys[1]);
        Assert.Equal(keys[0], keys[3]);
        Assert.NotEqual(keys[0], keys[2]);
        Assert.NotEqual(keys[0], keys[4]);
        Assert.NotEqual(keys[2], keys[4]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compatible_custom_endpoints_do_not_receive_provider_specific_cache_fields(bool router)
    {
        var requests = new List<string>();
        Func<HttpMessageHandler> factory = () => new CaptureHandler(requests);
        using IAiProvider provider = router
            ? new OpenRouterProvider("", "https://example.test", factory)
            : new OpenAiProvider("https://example.test", "", factory);
        var options = new ChatOptions("reasoning_effort", true, "high") { CacheSessionId = Guid.NewGuid().ToString("N") };
        await provider.ChatAsync("m", [new ChatMessage("user", "x")], options: options);
        await provider.ChatStreamAsync("m", [new ChatMessage("user", "x")], null, _ => { }, options: options);

        foreach (var body in requests)
        {
            using var json = JsonDocument.Parse(body);
            Assert.False(json.RootElement.TryGetProperty("prompt_cache_key", out _));
            Assert.False(json.RootElement.TryGetProperty("session_id", out _));
            Assert.Equal("high", json.RootElement.GetProperty("reasoning_effort").GetString());
        }
    }
}
