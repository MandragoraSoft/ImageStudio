using System.Net;
using System.Text.Json;
using ImageStudio.Services;

namespace ImageStudio.Tests;

public sealed class StreamingResponseDisposalTests
{
    private sealed class FailingContent(CancellationTokenSource cancellation, bool cancel) : HttpContent
    {
        public int DisposeCount { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            if (!cancel) return Task.FromException(new IOException("Błąd odczytu"));
            cancellation.Cancel();
            return Task.FromCanceled(cancellation.Token);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCount++;
            base.Dispose(disposing);
        }
    }

    private sealed class TrackedContent(string text) : StringContent(text)
    {
        public int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCount++;
            base.Dispose(disposing);
        }
    }

    private sealed class ScriptedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public List<string> Bodies { get; } = [];
        public Action? BeforeRetry { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Requests == 1) BeforeRetry?.Invoke();
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return responses[Requests++];
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Error_response_is_disposed_when_reading_fails(bool chatGpt, bool retry, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var failing = new FailingContent(cancellation, cancel);
        var first = new TrackedContent(chatGpt ? "reasoning" : "stream_options");
        var failedResponse = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = failing };
        var handler = retry
            ? new ScriptedHandler(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = first }, failedResponse)
            : new ScriptedHandler(failedResponse);
        var firstDisposedBeforeRetry = false;
        handler.BeforeRetry = () => firstDisposedBeforeRetry = first.DisposeCount == 1;
        var options = new ChatOptions("reasoning_effort", true, "high");
        Exception? error;

        if (chatGpt)
        {
            var session = new ChatGptSession("h", "c", "tok", "r", DateTimeOffset.UtcNow.AddHours(1)).ToJson();
            using var provider = new ChatGptProvider(session, null, null, "https://example.test/", handler);
            error = await Record.ExceptionAsync(() =>
                provider.ChatStreamAsync("m", [new ChatMessage("user", "x")], null, _ => { }, cancellation.Token, options));
        }
        else
        {
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
            error = await Record.ExceptionAsync(() =>
                OpenAiCompat.ChatStreamAsync(http, "chat", "m", [new ChatMessage("user", "x")], null, _ => { }, cancellation.Token, options));
        }

        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<HttpRequestException>(error);
        Assert.Equal(1, failing.DisposeCount);
        Assert.Equal(retry ? 2 : 1, handler.Requests);
        if (retry)
        {
            Assert.True(firstDisposedBeforeRetry);
            Assert.Equal(1, first.DisposeCount);
        }
        else first.Dispose();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Retry_disposes_both_responses_and_preserves_stream_or_error(bool chatGpt, bool retryFails)
    {
        var first = new TrackedContent(chatGpt ? "reasoning" : "stream_options");
        var second = new TrackedContent(retryFails ? """{"error":"drugi błąd"}""" : chatGpt
            ? "data: {\"type\":\"response.output_text.delta\",\"delta\":\"ok\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{}}\n\n"
            : "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n");
        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = first },
            new HttpResponseMessage(retryFails ? HttpStatusCode.Forbidden : HttpStatusCode.OK) { Content = second });
        var firstDisposedBeforeRetry = false;
        handler.BeforeRetry = () => firstDisposedBeforeRetry = first.DisposeCount == 1;
        var options = new ChatOptions("reasoning_effort", true, "high");
        ChatReply? reply = null;
        Exception? error;

        if (chatGpt)
        {
            var session = new ChatGptSession("h", "c", "tok", "r", DateTimeOffset.UtcNow.AddHours(1)).ToJson();
            using var provider = new ChatGptProvider(session, null, null, "https://example.test/", handler);
            error = await Record.ExceptionAsync(async () =>
                reply = await provider.ChatStreamAsync("m", [new ChatMessage("user", "x")], null, _ => { }, default, options));
        }
        else
        {
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
            error = await Record.ExceptionAsync(async () =>
                reply = await OpenAiCompat.ChatStreamAsync(http, "chat", "m", [new ChatMessage("user", "x")], null, _ => { }, default, options));
        }

        if (retryFails) Assert.Contains("HTTP 403", Assert.IsType<InvalidOperationException>(error).Message);
        else
        {
            Assert.Null(error);
            Assert.Equal("ok", reply!.Content);
        }
        Assert.True(firstDisposedBeforeRetry);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal(2, handler.Requests);
        using var initialBody = JsonDocument.Parse(handler.Bodies[0]);
        using var retryBody = JsonDocument.Parse(handler.Bodies[1]);
        var parameter = chatGpt ? "reasoning" : "stream_options";
        Assert.True(initialBody.RootElement.TryGetProperty(parameter, out _));
        Assert.False(retryBody.RootElement.TryGetProperty(parameter, out _));
    }
}
