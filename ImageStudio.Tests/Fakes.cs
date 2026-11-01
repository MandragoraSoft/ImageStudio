using System.Net;
using System.Text;
using ImageStudio.Services;

namespace ImageStudio.Tests;

// Provider do testów ViewModelu: tylko czat jest zaimplementowany, reszta rzuca wyjątek
internal sealed class FakeProvider : IAiProvider
{
    public Func<string, IReadOnlyList<ChatMessage>, string> OnChat { get; set; } = (_, _) => "";
    public string? LastModel { get; private set; }
    public List<ChatMessage> LastMessages { get; } = [];

    public string Id => "fake";
    public string DisplayName => "Fake";

    public Task<string> ChatAsync(string model, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        LastModel = model;
        LastMessages.Clear();
        LastMessages.AddRange(messages);
        return Task.FromResult(OnChat(model, messages));
    }

    public Task<ImageModels> GetImageModelsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<ImageModelInfo>> GetVideoModelsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<ChatModelInfo>> GetChatModelsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyDictionary<string, ModelPricing>> GetPricingAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<ImageResult> GenerateImageAsync(string model, string prompt, string size, ImageOptions? options,
                                                CancellationToken ct = default) => throw new NotSupportedException();

    public Task<ImageResult> EditImageAsync(string model, string prompt, IReadOnlyList<string> imagePaths,
                                            CancellationToken ct = default) => throw new NotSupportedException();

    public Task<ImageResult> GenerateVideoAsync(string model, string prompt, int duration, string aspectRatio,
                                                IReadOnlyList<(byte[] Bytes, string Mime)> attachments,
                                                Action<string>? progress = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PromptParts> EnhancePromptAsync(string model, string instruction, string system, string concept,
                                                IReadOnlyDictionary<string, object> parameters,
                                                CancellationToken ct = default) => throw new NotSupportedException();

    public void Dispose()
    {
    }
}

// Handler HTTP zwracający z góry zadaną odpowiedź i zapamiętujący żądanie
internal sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public string? RequestPath { get; private set; }
    public string? RequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        RequestPath = request.RequestUri?.AbsolutePath;
        RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

// Uruchamia kod w wątku STA (kontrolki WPF wymagają STA)
internal static class Sta
{
    public static void Run(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new AggregateException(error);
    }
}
