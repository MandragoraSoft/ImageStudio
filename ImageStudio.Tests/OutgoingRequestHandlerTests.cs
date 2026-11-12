using ImageStudio.Services;
using System.Collections.Concurrent;
using System.Net;

namespace ImageStudio.Tests;

public sealed class OutgoingRequestHandlerTests
{
    [Fact]
    public async Task Stop_cancels_all_parallel_requests_and_rejects_later_requests()
    {
        var registry = new OutgoingRequestRegistry();
        var started = new ConcurrentDictionary<string, TaskCompletionSource>();
        started["one"] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        started["two"] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = CreateClient(registry, async (request, token) =>
        {
            started[request.RequestUri!.AbsolutePath.TrimStart('/')].SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var one = client.GetAsync("https://example.test/one");
        var two = client.GetAsync("https://example.test/two");
        await Task.WhenAll(started.Values.Select(s => s.Task));

        registry.StopAndCancelAll();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => one);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => two);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("https://example.test/three"));
        Assert.True(await registry.WaitForIdleAsync(TimeSpan.FromSeconds(1)));
        Assert.Empty(registry.ActiveRequests);
    }

    [Fact]
    public async Task TracksResponseUntilEofWithoutExposingUrlOrHeaders()
    {
        var registry = new OutgoingRequestRegistry();
        using var client = CreateClient(registry, (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream([1, 2]))
            }));

        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://private.example/path/secret?token=secret")
            {
                Headers = { { "Authorization", "Bearer secret" } }
            },
            HttpCompletionOption.ResponseHeadersRead);

        var active = Assert.Single(registry.ActiveRequests);
        Assert.Equal("GET", active.Method);
        Assert.Equal("private.example", active.Host);
        Assert.DoesNotContain("secret", active.ToString());

        await using var stream = await response.Content.ReadAsStreamAsync();
        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty));
        Assert.Single(registry.ActiveRequests);
        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        Assert.Single(registry.ActiveRequests);
        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        Assert.Single(registry.ActiveRequests);
        Assert.Equal(0, await stream.ReadAsync(new byte[1]));
        Assert.Empty(registry.ActiveRequests);
        Assert.Equal(0, await stream.ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task CancelsBufferedResponseBodyAndNotifiesChanges()
    {
        var registry = new OutgoingRequestRegistry();
        var changes = 0;
        registry.Changed += (_, _) => Interlocked.Increment(ref changes);
        var bodyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = CreateClient(registry, (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new BlockingContent(bodyStarted) }));
        var send = client.GetAsync("https://example.test/response");
        await bodyStarted.Task;
        Assert.True(registry.Cancel(Assert.Single(registry.ActiveRequests).Id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.Empty(registry.ActiveRequests);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task CancelsOnlySelectedParallelRequest()
    {
        var registry = new OutgoingRequestRegistry();
        var started = new ConcurrentDictionary<string, TaskCompletionSource>();
        var releasePost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = CreateClient(registry, async (request, token) =>
        {
            started[request.Method.Method].TrySetResult();
            if (request.Method == HttpMethod.Get)
                await Task.Delay(Timeout.Infinite, token);
            await releasePost.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream([3]))
            };
        });
        started["GET"] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        started["POST"] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var get = client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.test/a"), HttpCompletionOption.ResponseHeadersRead);
        var post = client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://example.test/b"), HttpCompletionOption.ResponseHeadersRead);
        await Task.WhenAll(started.Values.Select(source => source.Task));

        var getId = Assert.Single(registry.ActiveRequests, request => request.Method == "GET").Id;
        var postId = Assert.Single(registry.ActiveRequests, request => request.Method == "POST").Id;
        Assert.True(registry.Cancel(getId));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => get);
        Assert.Contains(registry.ActiveRequests, request => request.Id == postId);
        Assert.DoesNotContain(registry.ActiveRequests, request => request.Id == getId);

        releasePost.SetResult();
        using var response = await post;
        Assert.Single(registry.ActiveRequests);
        await response.Content.ReadAsByteArrayAsync();
        Assert.Empty(registry.ActiveRequests);
    }

    [Fact]
    public async Task RequestBodyRemainsRegisteredAndCanBeCancelled()
    {
        var registry = new OutgoingRequestRegistry();
        var bodyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = CreateClient(registry, async (request, token) =>
        {
            await request.Content!.CopyToAsync(Stream.Null, null, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var send = client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "https://example.test/upload") { Content = new BlockingContent(bodyStarted) },
            HttpCompletionOption.ResponseHeadersRead);
        await bodyStarted.Task;

        var active = Assert.Single(registry.ActiveRequests);
        Assert.Equal("POST", active.Method);
        Assert.True(registry.Cancel(active.Id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.Empty(registry.ActiveRequests);
    }

    [Fact]
    public async Task DisposingResponseReleasesRequest()
    {
        var registry = new OutgoingRequestRegistry();
        using var client = CreateClient(registry, (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream([1, 2, 3]))
            }));
        var response = await client.GetAsync("https://example.test/stream", HttpCompletionOption.ResponseHeadersRead);

        Assert.Single(registry.ActiveRequests);
        response.Dispose();

        Assert.Empty(registry.ActiveRequests);
    }

    [Fact]
    public async Task RegistryCancellationInterruptsResponseStreamRead()
    {
        var registry = new OutgoingRequestRegistry();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = CreateClient(registry, (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingReadStream(readStarted))
            }));
        using var response = await client.GetAsync("https://example.test/stream", HttpCompletionOption.ResponseHeadersRead);
        var stream = await response.Content.ReadAsStreamAsync();
        var read = stream.ReadAsync(new byte[1]).AsTask();
        await readStarted.Task;

        var active = Assert.Single(registry.ActiveRequests);
        Assert.True(registry.Cancel(active.Id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Empty(registry.ActiveRequests);
    }

    [Fact]
    public async Task CallerCancellationIsLinkedToTheRequest()
    {
        var registry = new OutgoingRequestRegistry();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = CreateClient(registry, async (_, token) =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var callerCancellation = new CancellationTokenSource();

        var send = client.GetAsync("https://example.test/caller-cancel", callerCancellation.Token);
        await requestStarted.Task;
        Assert.Single(registry.ActiveRequests);
        callerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.Empty(registry.ActiveRequests);
    }

    [Fact]
    public async Task HandlerAndStreamErrorsReleaseRequest()
    {
        var registry = new OutgoingRequestRegistry();
        using (var client = CreateClient(registry, (_, _) => throw new InvalidOperationException("send failed")))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://example.test/error"));
            Assert.Empty(registry.ActiveRequests);
        }

        using (var client = CreateClient(registry, (_, _) =>
                   Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                   {
                       Content = new StreamContent(new FaultingStream())
                   })))
        using (var response = await client.GetAsync("https://example.test/body-error", HttpCompletionOption.ResponseHeadersRead))
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            await Assert.ThrowsAsync<IOException>(() => stream.ReadAsync(new byte[1]).AsTask());
            Assert.Empty(registry.ActiveRequests);
        }
    }

    private static HttpClient CreateClient(
        OutgoingRequestRegistry registry,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new OutgoingRequestHandler(registry) { InnerHandler = new StubHandler(send) });

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class BlockingContent(TaskCompletionSource started) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            System.Net.TransportContext? context,
            CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class FaultingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("read failed"));
    }

    private sealed class BlockingReadStream(TaskCompletionSource started) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
