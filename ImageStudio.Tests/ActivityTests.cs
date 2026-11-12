using ImageStudio.Configuration;
using ImageStudio.Services;

namespace ImageStudio.Tests;

public sealed class ActivityTests
{
    // Śledzi, co było aktywne w trakcie wywołania
    private sealed class RecordingTracker : IActivityTracker
    {
        public List<string> Started { get; } = [];
        public int Open { get; private set; }
        public bool IsActive => Open > 0;
        public bool DimsResponse => Open > 0 && !_noDim;
        private bool _noDim;
        public List<bool> DimFlags { get; } = [];
        public string Label => Started.LastOrDefault() ?? "";
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

        public IDisposable Begin(string label, bool dimResponse = true)
        {
            DimFlags.Add(dimResponse);
            _noDim = !dimResponse;
            Started.Add(label);
            Open++;
            return new Lease(this);
        }

        private sealed class Lease(RecordingTracker tracker) : IDisposable
        {
            public void Dispose() => tracker.Open--;
        }
    }

    [Fact]
    public void Tracker_is_active_until_every_operation_ends_and_labels_the_latest_one()
    {
        var tracker = new ActivityTracker();
        Assert.False(tracker.IsActive);
        Assert.Equal("", tracker.Label);

        var first = tracker.Begin("Generowanie obrazu");
        Assert.True(tracker.IsActive);
        Assert.Equal("Generowanie obrazu", tracker.Label);

        var second = tracker.Begin("Pobieranie cen");
        Assert.Equal("Pobieranie cen (+1)", tracker.Label);

        first.Dispose();
        first.Dispose();  // podwójne zwolnienie nie kończy cudzej operacji
        Assert.True(tracker.IsActive);
        Assert.Equal("Pobieranie cen", tracker.Label);

        second.Dispose();
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void Tracker_raises_property_changed_for_active_state_and_label()
    {
        var tracker = new ActivityTracker();
        var raised = new List<string>();
        tracker.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        using (tracker.Begin("x")) { }

        Assert.Equal(["IsActive", "Label", "DimsResponse", "IsActive", "Label", "DimsResponse"], raised);
    }

    private sealed class CapturingContext : SynchronizationContext
    {
        public int Posts { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            Posts++;
            d(state);
        }
    }

    [Fact]
    public void Tracker_notifies_on_the_captured_ui_context_when_operations_end_on_another_thread()
    {
        var context = new CapturingContext();
        SynchronizationContext.SetSynchronizationContext(context);
        ActivityTracker tracker;
        try { tracker = new ActivityTracker(); }
        finally { SynchronizationContext.SetSynchronizationContext(null); }

        var lease = tracker.Begin("z wątku tła");  // wątek testu nie jest wątkiem UI, więc powiadomienie idzie przez Post
        Assert.True(context.Posts > 0);
        lease.Dispose();
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public async Task Decorator_reports_every_api_call_and_ends_the_activity_afterwards()
    {
        var tracker = new RecordingTracker();
        var fake = new FakeProvider
        {
            ImageCatalog = new ImageModels([], []),
            VideoCatalog = [],
            ChatCatalog = [],
        };
        IAiProvider provider = new ActivityAiProvider(fake, tracker);

        await provider.GetImageModelsAsync();
        await provider.GetVideoModelsAsync();
        await provider.GetChatModelsAsync();
        await provider.ChatAsync("m", [new ChatMessage("user", "x")]);
        await provider.ReloadAsync();

        Assert.Equal(["Pobieranie modeli obrazów", "Pobieranie modeli wideo", "Pobieranie modeli tekstowych",
                      "Odpowiedź modelu", "Odświeżanie katalogu modeli"], tracker.Started);
        Assert.Equal(0, tracker.Open);
    }

    [Fact]
    public async Task Decorator_ends_the_activity_when_the_call_fails()
    {
        var tracker = new RecordingTracker();
        IAiProvider provider = new ActivityAiProvider(new FakeProvider(), tracker);  // atrapa nie obsługuje generowania

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.GenerateImageAsync("m", "p", "1x1", null));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.EditImageAsync("m", "p", []));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.GenerateVideoAsync("m", "p", 4, "16:9", []));

        Assert.Equal(["Generowanie obrazu", "Edycja obrazu", "Generowanie wideo"], tracker.Started);
        Assert.Equal(0, tracker.Open);
    }

    [Fact]
    public async Task Decorator_labels_prompt_enhancing_and_is_active_during_the_call()
    {
        var tracker = new RecordingTracker();
        var fake = new FakeProvider { Enhance = (_, _, concept) => new PromptParts("s", concept) };
        var sawActive = false;
        fake.Enhance = (_, _, concept) =>
        {
            sawActive = tracker.IsActive;
            return new PromptParts("s", concept);
        };
        IAiProvider provider = new ActivityAiProvider(fake, tracker);

        await provider.EnhancePromptAsync("m", "i", "s", "kot", new Dictionary<string, object>(), negative: "bez");

        Assert.True(sawActive);
        Assert.Equal("bez", fake.LastEnhanceNegative);
        Assert.Equal(["Przebudowa promptu"], tracker.Started);
    }

    [Fact]
    public void Decorator_forwards_identity_and_default_text_model()
    {
        var provider = new ActivityAiProvider(new UberLlmProvider("http://x", "k", "http://x"), new ActivityTracker());

        Assert.Equal("uberllm", provider.Id);
        Assert.Equal("UberLLM", provider.DisplayName);
        Assert.Equal(UberLlmProvider.DefaultTextModelId, provider.DefaultTextModel);
    }

    [Fact]
    public void Production_provider_factory_wraps_every_provider_in_the_activity_decorator()
    {
        var factory = new ProviderFactory(new ActivityTracker(), new MemorySecretStore());
        var settings = new AppSettings("http://x", "http://x", 1536, "k", "system");

        using var provider = factory.Create("uberllm", settings);

        Assert.IsType<ActivityAiProvider>(provider);
        Assert.Equal("uberllm", provider.Id);
    }

    [Fact]
    public async Task Chat_tool_call_is_reported_as_an_activity_while_it_runs()
    {
        var tracker = new RecordingTracker();
        var provider = new FakeProvider();
        var host = new FakeToolHost();
        var duringCall = false;
        host.OnCall = _ =>
        {
            duringCall = tracker.IsActive && tracker.Label == "Narzędzie fs__read";
            return "treść";
        };
        provider.OnReply = history => history.Any(m => m.Role == "tool")
            ? new ChatReply("gotowe", [])
            : new ChatReply("", [new ToolCall("c1", "fs__read", "{}")]);
        var chat = new ImageStudio.ViewModels.ChatViewModel(() => provider, ["m1"], (_, _) => { }, "", host,
            activity: tracker, modelInfo: id => new ChatModelInfo(id, []) { SupportsTools = true })
            { SelectedModel = "m1", Input = "czytaj" };

        await chat.SendCommand.ExecuteAsync(null);

        Assert.True(duringCall);
        Assert.Equal(0, tracker.Open);
    }

    [Fact]
    public async Task Decorator_forwards_edit_and_video_options_to_the_inner_provider()
    {
        var fake = new FakeProvider
        {
            OnEditImage = (_, _, _) => new ImageResult([1], null),
            OnGenerateVideo = (_, _, _, _, _) => new ImageResult([1], null),
        };
        IAiProvider provider = new ActivityAiProvider(fake, new RecordingTracker());
        var edit = new ImageEditOptions(1.5, "bez", _ => { });
        var video = new VideoOptions("bez", _ => { });

        await provider.EditImageAsync("m", "p", [], edit);
        await provider.GenerateVideoAsync("m", "p", 4, "16:9", [], video);

        Assert.Same(edit, fake.LastEditOptions);
        Assert.Same(video, fake.LastVideoOptions);
    }
}
