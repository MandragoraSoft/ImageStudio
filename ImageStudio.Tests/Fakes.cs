using System.Net;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

// Provider do testów ViewModelu: tylko czat jest zaimplementowany, reszta rzuca wyjątek
internal class FakeProvider : IAiProvider
{
    public Func<string, IReadOnlyList<ChatMessage>, string> OnChat { get; set; } = (_, _) => "";

    // Gdy ustawione, zastępuje OnChat i pozwala zwracać prośby o narzędzia
    public Func<IReadOnlyList<ChatMessage>, ChatReply>? OnReply { get; set; }

    public string? LastModel { get; private set; }
    public List<ChatMessage> LastMessages { get; } = [];
    public IReadOnlyList<ToolDefinition>? LastTools { get; private set; }
    public int ChatCalls { get; private set; }
    public ChatOptions? LastOptions { get; private set; }

    public string Id => "fake";
    public string DisplayName => "Fake";

    public Task<ChatReply> ChatAsync(string model, IReadOnlyList<ChatMessage> messages,
                                     IReadOnlyList<ToolDefinition>? tools = null, CancellationToken ct = default,
                                     ChatOptions? options = null)
    {
        LastOptions = options;
        ChatCalls++;
        LastModel = model;
        LastTools = tools;
        LastMessages.Clear();
        LastMessages.AddRange(messages);
        return Task.FromResult(OnReply?.Invoke(messages) ?? new ChatReply(OnChat(model, messages), []));
    }

    // Katalog dla testów przeładowania; null = dana część "nie jest obsługiwana" (jak dotąd)
    public ImageModels? ImageCatalog { get; set; }
    public IReadOnlyList<ImageModelInfo>? VideoCatalog { get; set; }
    public IReadOnlyList<ChatModelInfo>? ChatCatalog { get; set; }
    public int ImageCalls { get; private set; }

    public Task<ImageModels> GetImageModelsAsync(CancellationToken ct = default)
    {
        ImageCalls++;
        return ImageCatalog is { } catalog ? Task.FromResult(catalog) : throw new NotSupportedException();
    }
    public Task<IReadOnlyList<ImageModelInfo>> GetVideoModelsAsync(CancellationToken ct = default) =>
        VideoCatalog is { } catalog ? Task.FromResult(catalog) : throw new NotSupportedException();
    public Task<IReadOnlyList<ChatModelInfo>> GetChatModelsAsync(CancellationToken ct = default) =>
        ChatCatalog is { } catalog ? Task.FromResult(catalog) : throw new NotSupportedException();
    public IReadOnlyDictionary<string, ModelPricing>? PricingCatalog { get; set; }
    public Task<IReadOnlyDictionary<string, ModelPricing>> GetPricingAsync(CancellationToken ct = default) =>
        PricingCatalog is { } pricing ? Task.FromResult(pricing) : throw new NotSupportedException();

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
                                                IReadOnlyList<PromptRevision>? history = null,
                                                CancellationToken ct = default)
    {
        LastHistory = history;
        return Enhance?.Invoke(instruction, system, concept) is { } parts ? Task.FromResult(parts) : throw new NotSupportedException();
    }

    public IReadOnlyList<PromptRevision>? LastHistory { get; private set; }

    public Func<string, string, string, PromptParts?>? Enhance { get; set; }

    public void Dispose()
    {
    }
}

// Dostawca ze strumieniem: wysyła zadane kawałki, a po każdym wywołuje AfterDelta (test podgląda stan w trakcie),
// potem zwraca zwykłą odpowiedź (OnReply / OnChat). Bazowy FakeProvider nie ma strumienia i używa domyślnej implementacji
internal sealed class StreamingFakeProvider : FakeProvider, IAiProvider
{
    public List<ChatDelta> Deltas { get; } = [];
    public Action<ChatDelta>? AfterDelta { get; set; }
    public Exception? FailAfterDeltas { get; set; }
    public int StreamCalls { get; private set; }

    async Task<ChatReply> IAiProvider.ChatStreamAsync(string model, IReadOnlyList<ChatMessage> messages,
                                                       IReadOnlyList<ToolDefinition>? tools, Action<ChatDelta> onDelta,
                                                       CancellationToken ct, ChatOptions? options)
    {
        StreamCalls++;
        foreach (var delta in Deltas)
        {
            onDelta(delta);
            AfterDelta?.Invoke(delta);
        }

        if (FailAfterDeltas is { } error) throw error;
        return await ChatAsync(model, messages, tools, ct, options);
    }
}

// Okna dialogowe do testów: wybór plików i schowek z góry zadane
internal sealed class FakeDialogs : IDialogService
{
    public string[]? PickResult { get; set; }
    public BitmapSource? Clipboard { get; set; }
    public string? CopiedText { get; private set; }
    public void CopyText(string text) => CopiedText = text;

    public string[]? PickImageFiles() => PickResult;
    public BitmapSource? GetClipboardImage() => Clipboard;

    public string? ChooseSaveFile(string suggestedFileName) => throw new NotSupportedException();
    public void CopyImage(BitmapSource image) => throw new NotSupportedException();
    // Wynik okna ustawień na podstawie bieżących wartości; null = anulowano
    public Func<SettingsResult, SettingsResult?>? SettingsAnswer { get; set; }
    public IReadOnlyList<ISettingsTab>? LastSettingsTabs { get; private set; }
    public SettingsResult? ShowSettings(SettingsResult current, IReadOnlyList<ISettingsTab> tabs) =>
        (LastSettingsTabs = tabs) is not null && SettingsAnswer is { } answer ? answer(current) : throw new NotSupportedException();
    public Func<string, string, PresetDraft?>? SavePresetAnswer { get; set; }
    public PresetDraft? ShowSavePreset(string defaultName, string prompt = "", Func<Task<PresetDraft>>? suggest = null) =>
        SavePresetAnswer is { } answer ? answer(defaultName, prompt) : throw new NotSupportedException();
    public PromptEdit? PromptEditResult { get; set; }
    public Func<Task<string>>? LastDescribe { get; private set; }
    public ImageStudio.Configuration.Preset? OverwritePreset { get; set; }
    public PromptEdit? LastInitial { get; private set; }
    public Func<PromptEdit, Task<PromptEdit>>? LastRebuild { get; private set; }
    public PromptEdit? ShowPromptEditor(string title, PromptEdit initial, Func<PromptEdit, Task<PromptEdit>>? rebuild = null, object? enhancerContext = null, IReadOnlyList<ImageStudio.Configuration.Preset>? presets = null, Action<ImageStudio.Configuration.Preset>? savePreset = null, Func<Task<string>>? describeImages = null)
    {
        LastInitial = initial;
        LastDescribe = describeImages;
        LastRebuild = rebuild;
        // Symuluje "Nadpisz wybrany" w oknie: prompt z wyniku trafia do zestawu przez delegat zapisu
        if (OverwritePreset is { } preset && PromptEditResult is { } result && savePreset is not null)
        {
            preset.Prompt = result.Prompt;
            preset.SystemPrompt = result.System ?? "";
            savePreset(preset);
        }
        return PromptEditResult;
    }
    public ImageStudio.Configuration.Preset? ShowPresets() => throw new NotSupportedException();
    public bool ConfirmToolCall(ToolCall call) => true;
    public bool ConfirmAnswer { get; set; } = true;
    public bool Confirm(string text, string title) => ConfirmAnswer;
    public Action<ModelBrowserViewModel>? OnShowModelBrowser { get; set; }
    public void ShowModelBrowser(ModelBrowserViewModel viewModel) =>
        (OnShowModelBrowser ?? throw new NotSupportedException()).Invoke(viewModel);
}

// Obrazy testowe generowane w pamięci
internal static class TestImages
{
    public static BitmapSource Bitmap(int width, int height)
    {
        var pixels = new byte[width * height * 3];
        Array.Fill(pixels, (byte)180);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, width * 3);
        bitmap.Freeze();
        return bitmap;
    }

    public static byte[] Png(int width, int height) => Encode(new PngBitmapEncoder(), width, height);
    public static byte[] Jpeg(int width, int height) => Encode(new JpegBitmapEncoder(), width, height);

    private static byte[] Encode(BitmapEncoder encoder, int width, int height)
    {
        encoder.Frames.Add(BitmapFrame.Create(Bitmap(width, height)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static (int Width, int Height) Size(byte[] bytes)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }
}

// Źródło narzędzi do testów pętli czatu
internal sealed class FakeToolHost : IToolHost
{
    public IReadOnlyList<ToolDefinition> Tools { get; set; } = [new ToolDefinition("fs__read", "Czyta plik", """{"type":"object","properties":{}}""")];
    public bool RequireApproval { get; set; }
    public Func<ToolCall, string> OnCall { get; set; } = _ => "wynik";
    public List<ToolCall> Calls { get; } = [];

    public Task<string> CallAsync(ToolCall call, CancellationToken ct = default)
    {
        Calls.Add(call);
        return Task.FromResult(OnCall(call));
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
