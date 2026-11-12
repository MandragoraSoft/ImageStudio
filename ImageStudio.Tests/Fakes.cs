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

    // Opóźnia odpowiedź do chwili, gdy test ją zwolni albo anuluje (Stop)
    public Func<CancellationToken, Task>? Gate { get; set; }

    public async Task<ChatReply> ChatAsync(string model, IReadOnlyList<ChatMessage> messages,
                                           IReadOnlyList<ToolDefinition>? tools = null, CancellationToken ct = default,
                                           ChatOptions? options = null)
    {
        if (Gate is { } gate) await gate(ct);
        LastOptions = options;
        ChatCalls++;
        LastModel = model;
        LastTools = tools;
        LastMessages.Clear();
        LastMessages.AddRange(messages);
        return OnReply?.Invoke(messages) ?? new ChatReply(OnChat(model, messages), []);
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

    // Obraz i wideo: wywołania zapamiętane, wyniki zadane przez test; bez hooka dostawca "nie umie"
    public Func<string, string, string, ImageOptions?, ImageResult>? OnGenerateImage { get; set; }
    public Func<string, string, IReadOnlyList<string>, ImageResult>? OnEditImage { get; set; }
    public Func<string, string, int, string, int, ImageResult>? OnGenerateVideo { get; set; }
    public (string Model, string Prompt, string Size, ImageOptions? Options)? LastImage { get; private set; }
    public (string Model, string Prompt, IReadOnlyList<string> Paths)? LastEdit { get; private set; }
    public (string Model, string Prompt, int Duration, string Aspect, int Attachments)? LastVideo { get; private set; }

    public Task<ImageResult> GenerateImageAsync(string model, string prompt, string size, ImageOptions? options,
                                                CancellationToken ct = default)
    {
        LastImage = (model, prompt, size, options);
        return OnGenerateImage is { } run ? Task.FromResult(run(model, prompt, size, options)) : throw new NotSupportedException();
    }

    public Task<ImageResult> EditImageAsync(string model, string prompt, IReadOnlyList<string> imagePaths,
                                            CancellationToken ct = default)
    {
        LastEdit = (model, prompt, imagePaths);
        return OnEditImage is { } run ? Task.FromResult(run(model, prompt, imagePaths)) : throw new NotSupportedException();
    }

    // Edycja z opcjami (kształt wyniku): zapamiętuje opcje i działa jak zwykła edycja
    public ImageEditOptions? LastEditOptions { get; private set; }

    Task<ImageResult> IAiProvider.EditImageAsync(string model, string prompt, IReadOnlyList<string> imagePaths, ImageEditOptions? options,
                                                 CancellationToken ct)
    {
        LastEditOptions = options;
        return EditImageAsync(model, prompt, imagePaths, ct);
    }

    public Task<ImageResult> GenerateVideoAsync(string model, string prompt, int duration, string aspectRatio,
                                                IReadOnlyList<(byte[] Bytes, string Mime)> attachments,
                                                Action<string>? progress = null, CancellationToken ct = default)
    {
        LastVideo = (model, prompt, duration, aspectRatio, attachments.Count);
        return OnGenerateVideo is { } run ? Task.FromResult(run(model, prompt, duration, aspectRatio, attachments.Count)) : throw new NotSupportedException();
    }

    public Task<PromptParts> EnhancePromptAsync(string model, string instruction, string system, string concept,
                                                IReadOnlyDictionary<string, object> parameters,
                                                IReadOnlyList<PromptRevision>? history = null,
                                                CancellationToken ct = default)
    {
        LastHistory = history;
        return Enhance?.Invoke(instruction, system, concept) is { } parts ? Task.FromResult(parts) : throw new NotSupportedException();
    }

    public IReadOnlyList<PromptRevision>? LastHistory { get; private set; }

    // Dźwięk: katalogi i odpowiedzi zadane przez test; domyślnie dostawca "nie ma dźwięku" (puste listy)
    public List<AudioModelInfo> SpeechCatalog { get; } = [];
    public List<AudioModelInfo> TranscriptionCatalog { get; } = [];
    public Dictionary<string, string[]> VoiceCatalog { get; } = [];
    public Func<string, string, string?, double?, string, AudioResult>? OnSynthesize { get; set; }
    public Func<string, string, string?, string?, TranscriptResult>? OnTranscribe { get; set; }
    public (string Model, string Text, string? Voice, double? Speed, string Format)? LastSpeech { get; private set; }
    public (string Model, string Path, string? Language, string? Prompt)? LastTranscription { get; private set; }

    public Task<IReadOnlyList<AudioModelInfo>> GetSpeechModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AudioModelInfo>>(SpeechCatalog);

    public Task<IReadOnlyList<AudioModelInfo>> GetTranscriptionModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AudioModelInfo>>(TranscriptionCatalog);

    public Task<IReadOnlyList<string>> GetSpeechVoicesAsync(string model, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(VoiceCatalog.GetValueOrDefault(model) ?? []);

    public Task<AudioResult> SynthesizeAsync(string model, string text, string? voice, double? speed, string format,
                                             CancellationToken ct = default)
    {
        LastSpeech = (model, text, voice, speed, format);
        return OnSynthesize is { } run ? Task.FromResult(run(model, text, voice, speed, format)) : throw new NotSupportedException();
    }

    public Task<TranscriptResult> TranscribeAsync(string model, string filePath, string? language, string? prompt,
                                                  CancellationToken ct = default)
    {
        LastTranscription = (model, filePath, language, prompt);
        return OnTranscribe is { } run ? Task.FromResult(run(model, filePath, language, prompt)) : throw new NotSupportedException();
    }

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
    public string? AudioPick { get; set; }
    public string? SaveAsAnswer { get; set; }
    public string? PickAudioFile() => AudioPick;
    public List<string> Opened { get; } = [];
    public Exception? OpenError { get; set; }
    public void OpenFile(string path) => (OpenError is null ? Opened : throw OpenError).Add(path);
    public string? ChooseSaveFileAs(string suggestedFileName, string title, string filter) => SaveAsAnswer;
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
    // Okno zestawów: test podgląda kontekst z głównego okna (OnShowPresets) i zwraca wynik; bez hooka rzuca wyjątek
    public Func<ImageStudio.ViewModels.PresetsContext, ImageStudio.Configuration.Preset?>? OnShowPresets { get; set; }
    public ImageStudio.Configuration.Preset? ShowPresets(ImageStudio.ViewModels.PresetsContext? context = null) =>
        (OnShowPresets ?? throw new NotSupportedException())(context!);
    public bool ConfirmToolCall(ToolCall call) => true;
    public bool ConfirmAnswer { get; set; } = true;
    public int ConfirmCount { get; private set; }
    public bool Confirm(string text, string title) { ConfirmCount++; return ConfirmAnswer; }
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

// Odtwarzacz i nagrywarka bez sprzętu: zapamiętują wywołania, a test wywołuje zakończenie odtwarzania i poziom sam
internal sealed class FakeAudioPlayer : IAudioPlayer
{
    public List<string> Played { get; } = [];
    public int Stops { get; private set; }
    private Action? _ended;

    public void Play(string path, Action? ended = null, Action<string>? failed = null)
    {
        Played.Add(path);
        (_ended, _failed) = (ended, failed);
    }

    private Action<string>? _failed;
    public void Fail(string reason) => _failed?.Invoke(reason);

    public void Stop() => Stops++;
    public int OutputDevice { get; set; } = -1;
    public IReadOnlyList<AudioDevice> OutputDevices() => [new(-1, "Domyślne"), new(0, "Głośniki"), new(1, "Słuchawki")];
    public void FinishPlayback() => _ended?.Invoke();
}

internal sealed class FakeAudioRecorder : IAudioRecorder
{
    public List<AudioDevice> Mics { get; } = [new(0, "Mikrofon testowy")];
    public string? StartedPath { get; private set; }
    public int? StartedDevice { get; private set; }
    public Exception? StartError { get; set; }
    public bool Stopped { get; private set; }

    public event Action<double>? LevelChanged;

    public IReadOnlyList<AudioDevice> Devices() => Mics;

    public void Start(int deviceIndex, string path)
    {
        if (StartError is not null) throw StartError;
        (StartedDevice, StartedPath, Stopped) = (deviceIndex, path, false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);  // plik, który nagrywarka zostawiłaby na dysku
    }

    public Task StopAsync()
    {
        Stopped = true;
        return Task.CompletedTask;
    }

    public void Level(double value) => LevelChanged?.Invoke(value);
}
