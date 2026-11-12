using System.Net;
using System.Collections.Concurrent;
using System.Text.Json;
using ImageStudio.Configuration;
using ImageStudio.Composition;
using ImageStudio.Networking;
using ImageStudio.Services;
using ImageStudio.Storage;
using ImageStudio.Storage.Cloud;
using ImageStudio.Storage.OneDrive;
using Microsoft.Extensions.DependencyInjection;

namespace ImageStudio.Tests;

public sealed class OutgoingRequestIntegrationTests
{
    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private sealed class BlockingContent(TaskCompletionSource started) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class Secrets : IStorageSecrets
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? Load(string name) => Values.GetValueOrDefault(name);
        public void Save(string value, string name) => Values[name] = value;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData("uberllm")]
    [InlineData("openrouter")]
    [InlineData("openai")]
    [InlineData("chatgpt")]
    [InlineData("mistral")]
    [InlineData("kilo")]
    [InlineData("opencodego")]
    public async Task Provider_factory_routes_model_requests_through_registry(string id)
    {
        var started = Signal();
        var registry = new OutgoingRequestRegistry();
        var session = new ChatGptSession("h", "c", "a", "r", DateTimeOffset.UtcNow.AddHours(1)).ToJson();
        var settings = new AppSettings("https://example.test", "https://example.test", 1024, "key", "system", ChatGptSession: session);
        using var provider = ProviderRegistry.Create(id, settings, null, () => registry.CreateHandler(new Stub(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })));
        var request = provider.GetChatModelsAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(registry.Cancel(Assert.Single(registry.ActiveRequests).Id));
        await Assert.ThrowsAsync<UserCanceledRequestException>(() => request);
        Assert.Empty(registry.ActiveRequests);
    }

    [Fact]
    public async Task OpenAi_download_uses_a_separate_tracked_client_without_api_key()
    {
        var registry = new OutgoingRequestRegistry();
        var started = Signal();
        var handlers = 0;
        using var provider = new OpenAiProvider("https://example.test", "secret", () =>
        {
            Interlocked.Increment(ref handlers);
            return registry.CreateHandler(new Stub(async (request, ct) =>
            {
                if (request.RequestUri!.Host == "files.example.test")
                {
                    Assert.Null(request.Headers.Authorization);
                    started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("""{"data":[{"url":"https://files.example.test/image"}]}""") };
            }));
        });
        var request = provider.GenerateImageAsync("m", "p", "1024x1024", null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, handlers);
        Assert.True(registry.Cancel(Assert.Single(registry.ActiveRequests).Id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task OpenRouter_download_uses_a_separate_tracked_client_without_api_key()
    {
        var registry = new OutgoingRequestRegistry();
        var started = Signal();
        var handlers = 0;
        using var provider = new OpenRouterProvider("secret", "https://example.test", () =>
        {
            Interlocked.Increment(ref handlers);
            return registry.CreateHandler(new Stub(async (request, ct) =>
            {
                if (request.RequestUri!.Host == "files.example.test")
                {
                    Assert.Null(request.Headers.Authorization);
                    started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("""{"choices":[{"message":{"images":[{"image_url":{"url":"https://files.example.test/image"}}]}}]}""") };
            }));
        });
        var request = provider.GenerateImageAsync("m", "p", "1024x1024", null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, handlers);
        Assert.True(registry.Cancel(Assert.Single(registry.ActiveRequests).Id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task Uber_market_uses_a_distinct_tracked_client()
    {
        var registry = new OutgoingRequestRegistry();
        var started = Signal();
        var handlers = 0;
        using var provider = new UberLlmProvider("https://example.test", "key", "https://market.example.test", () =>
        {
            Interlocked.Increment(ref handlers);
            return registry.CreateHandler(new Stub(async (request, ct) =>
            {
                Assert.Equal("market.example.test", request.RequestUri!.Host);
                Assert.Null(request.Headers.Authorization);
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
        });
        var prices = provider.GetPricingAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, handlers);
        Assert.True(registry.Cancel(Assert.Single(registry.ActiveRequests).Id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prices);
    }

    [Fact]
    public async Task Cancelled_token_exchange_is_not_a_login_timeout()
    {
        var registry = new OutgoingRequestRegistry();
        var started = Signal();
        using var http = new HttpClient(registry.CreateHandler(new Stub(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })));
        var auth = new ChatGptAuth(http);
        var request = auth.RefreshAsync(new ChatGptSession("h", "c", "a", "r"), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(registry.Cancel(Assert.Single(registry.ActiveRequests).Id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task Cancelled_token_response_body_is_not_a_login_timeout()
    {
        var registry = new OutgoingRequestRegistry();
        var started = Signal();
        using var http = new HttpClient(registry.CreateHandler(new Stub((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new BlockingContent(started) }))));
        var auth = new ChatGptAuth(http);
        var request = auth.RefreshAsync(new ChatGptSession("h", "c", "a", "r"), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(registry.Cancel(Assert.Single(registry.ActiveRequests).Id));
        await Assert.ThrowsAsync<UserCanceledRequestException>(() => request);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed_as_a_pricing_timeout()
    {
        var registry = new OutgoingRequestRegistry();
        var started = Signal();
        using var provider = new OpenAiProvider("https://example.test", "key", () =>
            registry.CreateHandler(new Stub(async (request, ct) =>
            {
                if (request.RequestUri!.Host == "openrouter.ai")
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""") };
            })));
        var request = provider.GetPricingAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(registry.Cancel(Assert.Single(registry.ActiveRequests).Id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task Composition_uses_the_same_registry_for_provider_and_cloud_oauth()
    {
        var started = Signal();
        var registry = new OutgoingRequestRegistry(() => new Stub(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var secrets = new Secrets();
        secrets.Values["storage-onedrive"] = JsonSerializer.Serialize(new OAuthTokens("expired", "refresh", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var services = new ServiceCollection();
        services.AddImageStudioCore();
        services.AddSingleton(registry);
        services.AddSingleton<IStorageSecrets>(secrets);
        services.AddSingleton<ISecretStore>(new MemorySecretStore());
        using var container = services.BuildServiceProvider();
        var factory = container.GetRequiredService<IProviderFactory>();
        var settings = new AppSettings("https://example.test", "https://example.test", 1024, "key", "system");
        using var provider = factory.Create("mistral", settings);
        var models = provider.GetChatModelsAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(registry.Cancel(Assert.Single(registry.ActiveRequests).Id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => models);

        started = Signal();
        var cloud = container.GetRequiredService<OneDriveStorage>();
        var read = cloud.ReadRemoteAsync("a.png");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(registry.Cancel(Assert.Single(registry.ActiveRequests).Id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Empty(registry.ActiveRequests);
    }

    [Fact]
    public async Task Image_downloader_and_mcp_http_use_registry()
    {
        var registry = new OutgoingRequestRegistry();
        var started = Signal();
        HttpMessageHandler Inner() => new Stub(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var download = new ImageDownloadService(registry, Inner());
        var image = download.DownloadAsync("https://example.test/a.png");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(registry.Cancel(Assert.Single(registry.ActiveRequests).Id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => image);

        started = Signal();
        await using var host = new McpToolHost(registry, Inner);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connection = host.ConnectAsync("test", new McpServerConfig { Url = "https://example.test/mcp" }, stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(registry.Cancel(Assert.Single(registry.ActiveRequests).Id));
        await Assert.ThrowsAnyAsync<Exception>(() => connection);
        Assert.Empty(registry.ActiveRequests);
    }

    [Fact]
    public async Task Mcp_http_tracks_initialize_list_and_tool_call_without_network()
    {
        var registry = new OutgoingRequestRegistry();
        var methods = new ConcurrentQueue<string>();
        await using var host = new McpToolHost(registry, () => new Stub(async (request, ct) =>
        {
            Assert.NotEmpty(registry.ActiveRequests);
            Assert.Equal("example", request.Headers.GetValues("X-Test").Single());
            if (request.Method != HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = json.RootElement;
            var method = root.GetProperty("method").GetString()!;
            methods.Enqueue(method);
            if (!root.TryGetProperty("id", out var id))
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            object result = method switch
            {
                "initialize" => new { protocolVersion = "2025-03-26", capabilities = new { tools = new { } }, serverInfo = new { name = "fake", version = "1" } },
                "tools/list" => new { tools = new[] { new { name = "echo", description = "echo", inputSchema = new { type = "object" } } } },
                "tools/call" => new { content = new[] { new { type = "text", text = "ok" } } },
                _ => new { },
            };
            var body = method == "server/discover"
                ? JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "unknown" } })
                : JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var config = new McpServerConfig { Url = "https://example.test/mcp", Headers = { ["X-Test"] = "example" } };
        Assert.Equal(1, await host.ConnectAsync("fake", config, timeout.Token));
        Assert.Equal("ok", await host.CallAsync(new ToolCall("1", "fake__echo", "{}"), timeout.Token));
        Assert.Contains("initialize", methods);
        Assert.Contains("tools/list", methods);
        Assert.Contains("tools/call", methods);
        await host.DisconnectAsync("fake");
        Assert.Empty(registry.ActiveRequests);
    }

    [Fact]
    public async Task Cloud_upload_retries_after_request_cancellation()
    {
        var registry = new OutgoingRequestRegistry();
        var started = Signal();
        var retryStarted = Signal();
        var attempts = 0;
        var secrets = new Secrets();
        secrets.Values["storage-onedrive"] = JsonSerializer.Serialize(new OAuthTokens("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)));
        var handler = registry.CreateHandler(new Stub(async (_, ct) =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            if (attempt <= 2)
            {
                (attempt == 1 ? started : retryStarted).TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }));
        var storage = new OneDriveStorage(secrets, handler);
        var folder = Path.Combine(Path.GetTempPath(), "ImageStudio-request-" + Guid.NewGuid().ToString("N"));
        storage.Cache.Roots[MediaKind.Image] = folder;
        try
        {
            await storage.WriteAsync(MediaKind.Image, "image.png", new byte[] { 1 });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(registry.Cancel(Assert.Single(registry.ActiveRequests).Id));
            await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(registry.Cancel(Assert.Single(registry.ActiveRequests).Id));
            await storage.FlushAsync();
            Assert.Equal(3, attempts);
            Assert.NotEqual("storage.status.error", storage.Status?.Key);
            Assert.Empty(registry.ActiveRequests);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Cloud_timeout_is_not_retried_as_user_cancellation()
    {
        var attempts = 0;
        var secrets = new Secrets();
        secrets.Values["storage-onedrive"] = JsonSerializer.Serialize(new OAuthTokens("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)));
        var handler = new OutgoingRequestRegistry().CreateHandler(new Stub((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new TaskCanceledException("timeout");
        }));
        var storage = new OneDriveStorage(secrets, handler);
        var folder = Path.Combine(Path.GetTempPath(), "ImageStudio-request-" + Guid.NewGuid().ToString("N"));
        storage.Cache.Roots[MediaKind.Image] = folder;
        try
        {
            await storage.WriteAsync(MediaKind.Image, "image.png", new byte[] { 1 });
            await storage.FlushAsync();
            Assert.Equal(1, attempts);
            Assert.Equal("storage.status.error", storage.Status?.Key);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Cloud_delete_waits_for_cancelled_upload_retry()
    {
        var registry = new OutgoingRequestRegistry();
        var started = Signal();
        var calls = new ConcurrentQueue<HttpMethod>();
        var secrets = new Secrets();
        secrets.Values["storage-onedrive"] = JsonSerializer.Serialize(new OAuthTokens("access", null, DateTimeOffset.UtcNow.AddHours(1)));
        var handler = registry.CreateHandler(new Stub(async (request, ct) =>
        {
            calls.Enqueue(request.Method);
            if (calls.Count == 1)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }));
        var storage = new OneDriveStorage(secrets, handler);
        var folder = Path.Combine(Path.GetTempPath(), "ImageStudio-request-" + Guid.NewGuid().ToString("N"));
        storage.Cache.Roots[MediaKind.Image] = folder;
        try
        {
            var path = await storage.WriteAsync(MediaKind.Image, "image.png", new byte[] { 1 });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(registry.Cancel(Assert.Single(registry.ActiveRequests).Id));
            await storage.DeleteAsync(path);
            await storage.FlushAsync();
            Assert.Equal(new[] { HttpMethod.Put, HttpMethod.Put, HttpMethod.Delete }, calls.ToArray());
            Assert.False(File.Exists(path));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
