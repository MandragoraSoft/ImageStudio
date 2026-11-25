using System.Text.Json;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.Storage;
using ImageStudio.Storage.Local;

namespace ImageStudio.Tests;

// Domyślne ustawienia w pamięci
internal sealed class MemoryDefaults : Dictionary<string, string?>, IStudioDefaults
{
    public string ProviderId => "fake";
    public string? Model(string kind) => TryGetValue(kind, out var id) ? id : null;
    public void SetModel(string kind, string id) => this[kind] = id;
}

// Biblioteka w pamięci: jak kolekcje okna, ale bez UI
internal sealed class MemoryLibrary : IStudioLibrary
{
    public List<GalleryEntry> MediaItems { get; } = [];
    public List<GalleryEntry> VoiceItems { get; } = [];
    public IReadOnlyList<GalleryEntry> Media => MediaItems;
    public IReadOnlyList<GalleryEntry> Voice => VoiceItems;
    public void AddMedia(GalleryEntry entry) => MediaItems.Insert(0, entry);
    public void AddVoice(GalleryEntry entry) => VoiceItems.Insert(0, entry);

    public bool Remove(string id) => MediaItems.RemoveAll(e => e.Id == id) + VoiceItems.RemoveAll(e => e.Id == id) > 0;

    public void Clear(bool voice) => (voice ? VoiceItems : MediaItems).Clear();
}

// Operacje bez okna: dostawca-atrapa, magazyn w katalogu tymczasowym, biblioteka w pamięci
public sealed class StudioApiTests : IDisposable
{
    private sealed class PausedStorage(string id) : IMediaStoragePlugin
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => id;
        public string DisplayName => id;
        public IReadOnlyList<StorageSetting> Settings => [];
        public Task Started => _started.Task;
        public int Writes { get; private set; }
        public MediaKind? WrittenKind { get; private set; }
        public void Configure(IReadOnlyDictionary<string, string> values) { }
        public void Release() => _release.TrySetResult();

        public async Task<string> WriteAsync(MediaKind kind, string fileName, ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            Writes++;
            WrittenKind = kind;
            _started.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return $"{id}:{fileName}";
        }

        public Task<byte[]> ReadAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetLocalPathAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // Zapamiętuje odczyty i usunięcia, żeby test widział, która wtyczka obsłużyła wpis
    private sealed class RecordingStorage(string id) : IMediaStoragePlugin
    {
        public List<string> Read { get; } = [];
        public List<string> Deleted { get; } = [];

        public string Id => id;
        public string DisplayName => id;
        public IReadOnlyList<StorageSetting> Settings => [];
        public void Configure(IReadOnlyDictionary<string, string> values) { }
        public Task<string> WriteAsync(MediaKind kind, string fileName, ReadOnlyMemory<byte> data, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetLocalPathAsync(string location, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<byte[]> ReadAsync(string location, CancellationToken ct = default)
        {
            Read.Add(location);
            return Task.FromResult<byte[]>([1, 2]);
        }

        public Task DeleteAsync(string location, CancellationToken ct = default)
        {
            Deleted.Add(location);
            return Task.CompletedTask;
        }
    }

    private sealed class PlainStorage(IMediaStorage inner) : IMediaStorage
    {
        public Task<string> WriteAsync(MediaKind kind, string fileName, ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
            inner.WriteAsync(kind, fileName, data, ct);
        public Task<byte[]> ReadAsync(string location, CancellationToken ct = default) => inner.ReadAsync(location, ct);
        public Task<bool> ExistsAsync(string location, CancellationToken ct = default) => inner.ExistsAsync(location, ct);
        public Task DeleteAsync(string location, CancellationToken ct = default) => inner.DeleteAsync(location, ct);
        public Task<string> GetLocalPathAsync(string location, CancellationToken ct = default) => inner.GetLocalPathAsync(location, ct);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "is-studio-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProvider _provider = new();
    private readonly MemoryLibrary _library = new();
    private readonly MemoryPresetStore _presets = new();
    private readonly List<string> _asked = [];
    private readonly StudioApi _studio;

    public StudioApiTests()
    {
        Directory.CreateDirectory(_dir);
        var storage = new LocalMediaStorage
        {
            Roots = { [MediaKind.Image] = Path.Combine(_dir, "img"), [MediaKind.Video] = Path.Combine(_dir, "vid"), [MediaKind.Audio] = Path.Combine(_dir, "aud") },
        };
        _studio = new StudioApi(() => _provider, storage, _library, _presets, defaults: new MemoryDefaults { ["chat"] = "chat-1" })
        {
            Catalog = Catalog(),
        };
        _provider.OnGenerateImage = (_, _, _, _) => new ImageResult(TestImages.Png(8, 8), 0.04m);
        _provider.OnEditImage = (_, _, _) => new ImageResult(TestImages.Png(8, 8), null);
        _provider.OnGenerateVideo = (_, _, _, _, _) => new ImageResult([1, 2, 3], null);
        _provider.OnSynthesize = (_, text, _, _, format) => new AudioResult([1, 2], format, null);
        _provider.OnTranscribe = (_, _, _, _) => new TranscriptResult("  witaj świecie ", 0.01m);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ProviderCatalog Catalog() => new(
        new ImageModels([new ImageModelInfo("img-a", null, false, ["text"]), new ImageModelInfo("img-b", null, false, ["text"])],
                        [new ImageModelInfo("edit-a", null, false, ["text", "image"])]),
        [new ImageModelInfo("vid-a", null, false, ["text"])],
        [new ChatModelInfo("chat-1", [])],
        new Dictionary<string, ModelPricing>
        {
            ["vid-a"] = new(0.1m, null, null, "second", null, null),
            ["edit-a"] = new(0.05m, null, null, "image", null, null),
        },
        [],
        [new AudioModelInfo("tts-a", "owner", new ModelPricing(0.2m, null, null, "1K characters", null, null))],
        [new AudioModelInfo("stt-a", "owner")]);

    private string Wav() => TempFile("a.wav", [1, 2, 3]);

    private string TempFile(string name, byte[] data)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static readonly ImageOptions NoOptions = new(null, null, null);

    [Fact]
    public void Available_kinds_follow_the_catalog()
    {
        Assert.Equal(["generate", "edit", "video", "speech", "transcription", "chat"], _studio.AvailableKinds());

        _studio.Catalog = new ProviderCatalog(null, null, null, new Dictionary<string, ModelPricing>(), []);
        Assert.Empty(_studio.AvailableKinds());

        _studio.Catalog = null;
        Assert.Empty(_studio.AvailableKinds());
    }

    [Fact]
    public async Task Generate_image_saves_the_file_and_adds_a_library_entry()
    {
        var result = await _studio.GenerateImageAsync(new ImageRequest("kot", "realizm", null, 512, 256, new ImageOptions(7, "rozmycie", "high")));
        var entry = result.Entry;

        Assert.Equal(("img-a", "realizm\n\nkot", "512x256"), (_provider.LastImage!.Value.Model, _provider.LastImage.Value.Prompt, _provider.LastImage.Value.Size));
        Assert.Equal(new ImageOptions(7, "rozmycie", "high"), _provider.LastImage.Value.Options! with { NegativeNotice = null });
        Assert.True(File.Exists(entry.FilePath));
        Assert.Equal(0.04m, entry.CostUsd);
        Assert.Equal("Fake", entry.Provider);  // dostawca trafia do wpisu (dymek galerii)
        Assert.Equal("local", entry.StorageId);
        Assert.NotNull(result.Bytes);
        Assert.Same(entry, Assert.Single(_library.MediaItems));
    }

    [Theory]
    [InlineData(MediaKind.Image)]
    [InlineData(MediaKind.Video)]
    [InlineData(MediaKind.Audio)]
    public async Task Switching_storage_during_write_keeps_the_writer_and_its_identity(MediaKind kind)
    {
        var cloud = new PausedStorage("cloud");
        var local = new PausedStorage("local");
        var host = new MediaStorageHost([cloud, local]);
        var studio = new StudioApi(() => _provider, host, _library, _presets) { Catalog = Catalog() };

        var operation = kind switch
        {
            MediaKind.Image => studio.GenerateImageAsync(new ImageRequest("kot", Publish: false)),
            MediaKind.Video => studio.GenerateVideoAsync(new VideoGenerationRequest("fala", Publish: false)),
            _ => studio.SpeakAsync(new SpeechRequest("cześć", Publish: false)),
        };
        try
        {
            await cloud.Started.WaitAsync(TimeSpan.FromSeconds(5));
            host.Apply("local", null);
        }
        finally
        {
            cloud.Release();
            local.Release();
        }

        var result = await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("cloud", result.Entry.StorageId);
        Assert.StartsWith("cloud:", result.Entry.FilePath);
        Assert.Equal(kind, cloud.WrittenKind);
        Assert.Equal(1, cloud.Writes);
        Assert.Equal(0, local.Writes);
    }

    [Fact]
    public async Task Non_plugin_storage_remains_supported_without_an_identifier()
    {
        var backing = new PausedStorage("backing");
        backing.Release();
        var studio = new StudioApi(() => _provider, new PlainStorage(backing), _library, _presets) { Catalog = Catalog() };

        var result = await studio.GenerateImageAsync(new ImageRequest("kot", Publish: false));

        Assert.Null(result.Entry.StorageId);
        Assert.StartsWith("backing:", result.Entry.FilePath);
        Assert.Equal(1, backing.Writes);
    }

    [Fact]
    public async Task Delete_and_export_use_the_owner_not_the_active_plugin()
    {
        var cloud = new RecordingStorage("cloud");
        var local = new RecordingStorage("local");
        var host = new MediaStorageHost([local, cloud]); // aktywna jest local
        var studio = new StudioApi(() => _provider, host, _library, _presets) { Catalog = Catalog() };
        var entry = new GalleryEntry { Kind = "generate", FilePath = "cloud:a.png", StorageId = "cloud" };
        _library.AddMedia(entry);

        var target = Path.Combine(_dir, "eksport.png");
        await studio.ExportAsync(entry.Id, target);
        await studio.DeleteEntryAsync(entry.Id);

        Assert.Equal(["cloud:a.png"], cloud.Read);
        Assert.Equal(["cloud:a.png"], cloud.Deleted);
        Assert.Empty(local.Read);
        Assert.Empty(local.Deleted);
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task Unpublished_results_stay_out_of_the_library_so_the_host_can_add_them()
    {
        var image = await _studio.GenerateImageAsync(new ImageRequest("kot", Publish: false));
        var video = await _studio.GenerateVideoAsync(new VideoGenerationRequest("fala", Publish: false));
        var speech = await _studio.SpeakAsync(new SpeechRequest("cześć", Publish: false));
        var transcript = await _studio.TranscribeAsync(new TranscriptionRequest(Wav(), Publish: false));

        Assert.All(new[] { image, video, speech, transcript }, r => Assert.NotEmpty(r.Entry.FilePath));
        Assert.Empty(_library.MediaItems);
        Assert.Empty(_library.VoiceItems);
    }

    [Fact]
    public void Defaults_report_the_provider_and_models_and_save_only_models_from_the_catalog()
    {
        var defaults = new MemoryDefaults { ["edit"] = "edit-a" };
        var studio = new StudioApi(() => _provider, new LocalMediaStorage(), _library, _presets, defaults: defaults) { Catalog = Catalog() };

        var info = studio.GetDefaults();
        studio.SetDefaultModel("edit", "EDIT-A");
        studio.SetDefaultModel("speech", "tts-a");

        Assert.Equal(("fake", "Fake", "edit-a"), (info.Provider, info.ProviderName, info.Models["edit"]));
        Assert.Null(info.Models["video"]);
        Assert.Equal("edit-a", defaults["edit"]);
        Assert.Equal("tts-a", defaults["speech"]);
        Assert.Throws<ArgumentException>(() => studio.SetDefaultModel("edit", "nie-ma"));
        Assert.Throws<ArgumentException>(() => studio.SetDefaultModel("audio", "x"));
        Assert.Throws<InvalidOperationException>(() => new StudioApi(() => _provider, new LocalMediaStorage(), _library, _presets) { Catalog = Catalog() }.SetDefaultModel("edit", "edit-a"));
    }

    [Fact]
    public async Task A_stale_default_model_falls_back_to_the_first_in_the_catalog()
    {
        var studio = new StudioApi(() => _provider, new LocalMediaStorage { Roots = { [MediaKind.Image] = Path.Combine(_dir, "img") } }, _library, _presets,
                                   defaults: new MemoryDefaults { ["generate"] = "usuniety/model" }) { Catalog = Catalog() };

        await studio.GenerateImageAsync(new ImageRequest("kot", Publish: false));

        Assert.Equal("img-a", _provider.LastImage!.Value.Model);
    }

    [Fact]
    public async Task Generate_image_rejects_unknown_model_and_missing_models()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _studio.GenerateImageAsync(new ImageRequest("kot", Model: "nie-ma")));
        Assert.Empty(_library.MediaItems);

        _studio.Catalog = new ProviderCatalog(null, null, null, new Dictionary<string, ModelPricing>(), []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _studio.GenerateImageAsync(new ImageRequest("kot")));
    }

    [Fact]
    public async Task Edit_image_takes_local_references_and_estimates_cost()
    {
        var reference = TempFile("ref.png", TestImages.Png(4, 4));

        var entry = (await _studio.EditImageAsync(new EditRequest("jaśniej", null, [reference]))).Entry;

        Assert.Equal("edit-a", _provider.LastEdit!.Value.Model);
        Assert.Single(_provider.LastEdit.Value.Paths);
        Assert.Equal(0.05m, entry.CostUsd);
        Assert.True(entry.CostEstimated);
        await Assert.ThrowsAsync<ArgumentException>(() => _studio.EditImageAsync(new EditRequest("x", null, [])));
        await Assert.ThrowsAsync<ArgumentException>(() => _studio.EditImageAsync(new EditRequest("x", null, [Path.Combine(_dir, "brak.png")])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _studio.EditImageAsync(new EditRequest("x", null, ["https://x.test/a.png"])));
    }

    [Fact]
    public async Task Edit_image_fits_the_result_when_both_dimensions_are_given()
    {
        var reference = TempFile("ref.png", TestImages.Png(4, 4));

        var result = await _studio.EditImageAsync(new EditRequest("x", null, [reference], Width: 16, Height: 8));

        Assert.Equal((16, 8), TestImages.Size(result.Bytes!));
        Assert.Equal("16x8", result.Entry.Size);
    }

    [Fact]
    public async Task Edit_image_without_dimensions_keeps_the_proportions_of_the_first_reference()
    {
        var portrait = TempFile("portrait.png", TestImages.Png(40, 80));

        var result = await _studio.EditImageAsync(new EditRequest("ciemne włosy", null, [portrait]));

        // dostawca zwrócił kwadrat 8x8, a wynik ma wrócić do pionowych proporcji źródła
        Assert.Equal((40, 80), TestImages.Size(result.Bytes!));
        Assert.Equal("40x80", result.Entry.Size);
    }

    [Fact]
    public async Task Edit_image_limits_the_long_side_of_a_large_reference()
    {
        var wide = TempFile("wide.png", TestImages.Png(3200, 1600));

        var result = await _studio.EditImageAsync(new EditRequest("x", null, [wide]));

        Assert.Equal("1536x768", result.Entry.Size);
        Assert.Equal((1536, 768), TestImages.Size(result.Bytes!));
    }

    [Fact]
    public async Task Generate_video_passes_attachments_reports_progress_and_validates_options()
    {
        var picture = TempFile("p.png", TestImages.Png(4, 4));

        var entry = (await _studio.GenerateVideoAsync(new VideoGenerationRequest("fala", null, 8, "9:16", [picture]))).Entry;

        Assert.Equal(("vid-a", "fala", 8, "9:16", 1), _provider.LastVideo!.Value);
        Assert.Equal(0.8m, entry.CostUsd);
        Assert.Equal("video", entry.Kind);
        Assert.Contains("9:16", entry.Size);
        await Assert.ThrowsAsync<ArgumentException>(() => _studio.GenerateVideoAsync(new VideoGenerationRequest("x", Duration: 99)));
        await Assert.ThrowsAsync<ArgumentException>(() => _studio.GenerateVideoAsync(new VideoGenerationRequest("x", AspectRatio: "21:9")));
    }

    [Fact]
    public async Task Edit_negative_prompt_reaches_the_provider_and_is_recorded_when_sent()
    {
        var reference = TempFile("ref.png", TestImages.Png(4, 4));

        var result = await _studio.EditImageAsync(new EditRequest("jaśniej", null, [reference], NegativePrompt: "  rozmycie "));

        Assert.Equal("rozmycie", _provider.LastEditOptions!.NegativePrompt);
        Assert.Equal("jaśniej", _provider.LastEdit!.Value.Prompt);  // bez tekstowego fallbacku
        Assert.Null(result.Warnings);
        Assert.Equal("rozmycie", result.Entry.Params!["negative"]);
    }

    [Theory]
    [InlineData(NegativePromptOutcome.Unsupported)]
    [InlineData(NegativePromptOutcome.Unconfirmed)]
    [InlineData(NegativePromptOutcome.Rejected)]
    public async Task Edit_and_video_warn_and_do_not_record_a_negative_prompt_that_was_not_sent(NegativePromptOutcome outcome)
    {
        _provider.NegativeOutcome = outcome;
        var reference = TempFile("ref.png", TestImages.Png(4, 4));

        var edit = await _studio.EditImageAsync(new EditRequest("x", null, [reference], NegativePrompt: "bez"));
        var progress = new List<string>();
        var video = await _studio.GenerateVideoAsync(new VideoGenerationRequest("x", Progress: progress.Add, NegativePrompt: "bez"));

        Assert.Single(edit.Warnings!);
        Assert.Contains("edit-a", edit.Warnings![0]);
        Assert.Single(video.Warnings!);
        Assert.Contains("vid-a", video.Warnings![0]);
        Assert.Contains(video.Warnings[0], progress);
        Assert.False(edit.Entry.Params?.ContainsKey("negative") ?? false);
        Assert.False(video.Entry.Params?.ContainsKey("negative") ?? false);
        Assert.Equal("x", _provider.LastVideo!.Value.Prompt);
    }

    [Fact]
    public async Task Generate_image_warns_and_does_not_record_a_negative_prompt_that_was_not_sent()
    {
        var sent = await _studio.GenerateImageAsync(new ImageRequest("kot", null, null, 512, 256, new ImageOptions(null, " bez ", null)));
        _provider.NegativeOutcome = NegativePromptOutcome.Unsupported;
        var blank = await _studio.GenerateImageAsync(new ImageRequest("kot", null, null, 512, 256, new ImageOptions(null, "  ", null)));
        var skipped = await _studio.GenerateImageAsync(new ImageRequest("kot", null, null, 512, 256, new ImageOptions(null, "bez", null)));

        Assert.Equal("bez", sent.Entry.Params!["negative"]);
        Assert.Null(sent.Warnings);
        Assert.Null(blank.Warnings);
        Assert.False(blank.Entry.Params!.ContainsKey("negative"));
        Assert.Single(skipped.Warnings!);
        Assert.Contains("img-a", skipped.Warnings![0]);
        Assert.False(skipped.Entry.Params!.ContainsKey("negative"));
    }

    [Fact]
    public async Task Video_system_prompt_is_composed_into_the_text_prompt_and_blank_changes_nothing()
    {
        var plain = await _studio.GenerateVideoAsync(new VideoGenerationRequest(" fala ", SystemPrompt: "  "));
        Assert.Equal(" fala ", _provider.LastVideo!.Value.Prompt);  // zgodność wstecz: bez systemu prompt idzie bez zmian

        var composed = await _studio.GenerateVideoAsync(new VideoGenerationRequest("fala", SystemPrompt: " kamera: dron "));

        Assert.Equal("kamera: dron\n\nfala", _provider.LastVideo!.Value.Prompt);
        Assert.Equal("kamera: dron\n\nfala", composed.Entry.Prompt);
        Assert.Equal(" fala ", plain.Entry.Prompt);
    }

    [Fact]
    public async Task Blank_negative_prompt_is_not_passed_to_edit_or_video()
    {
        _provider.NegativeOutcome = NegativePromptOutcome.Unsupported;
        var reference = TempFile("ref.png", TestImages.Png(4, 4));

        var edit = await _studio.EditImageAsync(new EditRequest("x", null, [reference], NegativePrompt: "  "));
        var video = await _studio.GenerateVideoAsync(new VideoGenerationRequest("x", NegativePrompt: ""));

        Assert.Null(edit.Warnings);
        Assert.Null(video.Warnings);
        Assert.Null(_provider.LastVideoOptions?.NegativePrompt);
    }
    [Fact]
    public async Task Speak_writes_audio_and_translates_when_asked()
    {
        _provider.OnChat = (_, _) => "Hello";
        var stages = new List<string>();

        var plain = (await _studio.SpeakAsync(new SpeechRequest(" Cześć ", null, "alloy", 1.0, "wav"))).Entry;
        var translated = (await _studio.SpeakAsync(new SpeechRequest("Cześć", "tts-a", null, 1.5, Language: "en", Stage: stages.Add))).Entry;

        Assert.Equal(("tts-a", "Hello", null, 1.5, "mp3"), _provider.LastSpeech!.Value);
        Assert.EndsWith(".wav", plain.FilePath);
        Assert.True(File.Exists(plain.FilePath));
        Assert.Equal("Cześć", plain.Prompt);
        Assert.Equal("Hello", translated.Prompt);
        Assert.Contains("en", translated.Size);
        Assert.Equal(["translating", "synthesizing"], stages);
        Assert.Equal([translated, plain], _library.VoiceItems);
        await Assert.ThrowsAsync<ArgumentException>(() => _studio.SpeakAsync(new SpeechRequest("x", Format: "ogg")));
        await Assert.ThrowsAsync<ArgumentException>(() => _studio.SpeakAsync(new SpeechRequest("x", Language: "xx")));
        await Assert.ThrowsAsync<ArgumentException>(() => _studio.SpeakAsync(new SpeechRequest("x", Speed: 9)));
        await Assert.ThrowsAsync<ArgumentException>(() => _studio.SpeakAsync(new SpeechRequest("  ")));
    }

    [Fact]
    public async Task Speak_stops_on_an_empty_translation()
    {
        _provider.OnChat = (_, _) => "  ";

        await Assert.ThrowsAsync<InvalidOperationException>(() => _studio.SpeakAsync(new SpeechRequest("Cześć", Language: "fr")));

        Assert.Null(_provider.LastSpeech);
    }

    [Fact]
    public async Task Transcribe_returns_the_trimmed_text_and_keeps_the_file()
    {
        var path = Wav();

        var entry = (await _studio.TranscribeAsync(new TranscriptionRequest(path, null, "pl", "Gdańsk"))).Entry;

        Assert.Equal(("stt-a", path, "pl", "Gdańsk"), _provider.LastTranscription!.Value);
        Assert.Equal("witaj świecie", entry.Prompt);
        Assert.Equal("transcript", entry.Kind);
        await Assert.ThrowsAsync<ArgumentException>(() => _studio.TranscribeAsync(new TranscriptionRequest(Path.Combine(_dir, "brak.wav"))));
    }

    [Fact]
    public async Task Audio_models_and_voices_are_listed()
    {
        _provider.VoiceCatalog["tts-a"] = ["alloy", "nova"];

        var speech = await _studio.AudioModelsAsync("speech", "tts", 10);

        Assert.Equal("tts-a", Assert.Single(speech).Id);
        Assert.Equal(["alloy", "nova"], await _studio.VoicesAsync("tts-a"));
        Assert.Empty(await _studio.AudioModelsAsync("transcription", "brak", 10));
    }

    [Fact]
    public async Task Rebuild_prompt_passes_parameters_and_history_and_changes_nothing()
    {
        _provider.EnhanceWithNegative = (instruction, system, concept, negative) => new PromptParts(system + "+", concept + "!", instruction, negative + "!");
        var history = new List<PromptRevision> { new("a", "b", "c", "d", "e") };

        var custom = await _studio.RebuildPromptAsync(new PromptRebuildRequest("kot", Instruction: "po swojemu", Model: "inny"));
        var parts = await _studio.RebuildPromptAsync(new PromptRebuildRequest("kot", "styl", null, null, History: history, NegativePrompt: "tekst"));

        Assert.Equal(new PromptParts("styl+", "kot!", StudioDefaults.PromptInstruction, "tekst!"), parts);
        Assert.Equal("tekst", _provider.LastEnhanceNegative);
        Assert.Equal("po swojemu", custom.Changes);
        Assert.Equal(history, _provider.LastHistory);
        Assert.Empty(_library.MediaItems);
    }

    [Fact]
    public async Task Describe_images_downscales_and_returns_the_model_text()
    {
        var big = TempFile("big.png", TestImages.Png(400, 200));
        _provider.OnChat = (_, _) => "  Kot na parapecie.  ";

        var text = await _studio.DescribeImagesAsync(new DescribeRequest([big], "vision", 100));

        Assert.Equal("Kot na parapecie.", text);
        Assert.Equal("vision", _provider.LastModel);
        var image = _provider.LastMessages.Single(m => m.Role == "user").Images!.Single();
        Assert.Equal((100, 50), TestImages.Size(image.Bytes));
        await Assert.ThrowsAsync<ArgumentException>(() => _studio.DescribeImagesAsync(new DescribeRequest([], "vision", 100)));
    }

    [Fact]
    public void Presets_are_saved_found_and_overwritten()
    {
        _studio.SavePreset(new SavePresetRequest("Portret", "generate", "twarz", "styl", "img-b", 640, 480, new ImageOptions(3, "bez", "low")));
        _studio.SavePreset(new SavePresetRequest("portret", "generate", "twarz 2"));

        var preset = Assert.Single(_studio.Presets("generate"));
        Assert.Equal(("twarz 2", "", 1024), (preset.Prompt, preset.SystemPrompt, preset.Width));
        Assert.Empty(_studio.Presets("edit"));
        Assert.Equal("Portret", _studio.FindPreset("PORTRET").Name);
        Assert.Throws<ArgumentException>(() => _studio.FindPreset("brak"));
        Assert.Throws<ArgumentException>(() => _studio.SavePreset(new SavePresetRequest("x", "video", "p")));
    }

    [Fact]
    public async Task Library_lists_filters_removes_deletes_exports_and_clears()
    {
        var image = (await _studio.GenerateImageAsync(new ImageRequest("kot"))).Entry;
        var speech = (await _studio.SpeakAsync(new SpeechRequest("cześć"))).Entry;
        var transcript = (await _studio.TranscribeAsync(new TranscriptionRequest(Wav()))).Entry;

        Assert.Equal(3, _studio.List(null, 10).Count);
        Assert.Equal([image.Id], _studio.List("media", 10).Select(e => e.Id));
        Assert.Equal(2, _studio.List("voice", 10).Count);
        Assert.Equal([transcript.Id], _studio.List("transcript", 10).Select(e => e.Id));

        var target = Path.Combine(_dir, "out", "kopia.png");
        Assert.Equal(target, await _studio.ExportAsync(image.Id, target));
        Assert.Equal(await File.ReadAllBytesAsync(image.FilePath), await File.ReadAllBytesAsync(target));

        await Assert.ThrowsAsync<ArgumentException>(() => _studio.DeleteEntryAsync(transcript.Id));
        await _studio.DeleteEntryAsync(speech.Id);
        Assert.False(File.Exists(speech.FilePath));
        Assert.DoesNotContain(speech, _library.VoiceItems);

        _studio.RemoveEntry(transcript.Id);
        Assert.True(File.Exists(Path.Combine(_dir, "a.wav")));
        Assert.Empty(_library.VoiceItems);
        Assert.Throws<ArgumentException>(() => _studio.RemoveEntry("brak"));

        _studio.Clear("media");
        Assert.Empty(_library.MediaItems);
        Assert.True(File.Exists(image.FilePath));
        Assert.Throws<ArgumentException>(() => _studio.Clear("wszystko"));
    }
}

// Zestaw narzędzi nad StudioApi: widoczność wg katalogu, zgoda, parametry i wynik
public sealed class StudioToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "is-studiotools-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProvider _provider = new();
    private readonly MemoryLibrary _library = new();
    private readonly List<ToolCall> _confirms = [];
    private bool _approve = true;
    private readonly StudioApi _studio;
    private readonly StudioTools _tools;

    public StudioToolsTests()
    {
        Directory.CreateDirectory(_dir);
        var storage = new LocalMediaStorage { Roots = { [MediaKind.Image] = _dir, [MediaKind.Video] = _dir, [MediaKind.Audio] = _dir } };
        _studio = new StudioApi(() => _provider, storage, _library, new MemoryPresetStore())
        {
            Catalog = new ProviderCatalog(new ImageModels([new ImageModelInfo("img-a", null, false, ["text"])], []), null, [],
                                          new Dictionary<string, ModelPricing>(), [],
                                          [new AudioModelInfo("tts-a", null)], null),
        };
        _tools = new StudioTools(_studio, call =>
        {
            _confirms.Add(call);
            return _approve;
        });
        _provider.OnGenerateImage = (_, _, _, _) => new ImageResult(TestImages.Png(4, 4), null);
        _provider.OnSynthesize = (_, _, _, _, format) => new AudioResult([1], format, null);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Only_tools_with_models_are_visible_but_all_are_listed()
    {
        var visible = _tools.Definitions.Select(d => d.Name).Order().ToList();

        Assert.Contains("generate_image", visible);
        Assert.Contains("speak", visible);
        Assert.Contains("list_voices", visible);
        Assert.DoesNotContain("edit_image", visible);
        Assert.DoesNotContain("generate_video", visible);
        Assert.DoesNotContain("transcribe", visible);
        Assert.DoesNotContain("rebuild_prompt", visible);
        Assert.Contains("list_gallery", visible);
        Assert.Equal(20, _tools.AllDefinitions.Count);
        foreach (var definition in _tools.AllDefinitions)
        {
            using var schema = JsonDocument.Parse(definition.ParametersJson);
            Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task Hidden_tools_are_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _tools.CallAsync("transcribe", """{"path":"x"}""", true));
        await Assert.ThrowsAsync<ArgumentException>(() => _tools.CallAsync("nie_ma", "{}", true));
    }

    [Theory]
    [InlineData("speak", true)]
    [InlineData("transcribe", true)]
    [InlineData("generate_image", true)]
    [InlineData("edit_image", true)]
    [InlineData("generate_video", true)]
    [InlineData("delete_gallery_entry", true)]
    [InlineData("clear_gallery", true)]
    [InlineData("export_gallery_entry", true)]
    [InlineData("list_gallery", false)]
    [InlineData("rebuild_prompt", false)]
    [InlineData("save_preset", false)]
    [InlineData("remove_gallery_entry", false)]
    public void Costly_and_irreversible_tools_need_approval(string name, bool costly) => Assert.Equal(costly, _tools.IsCostly(name));

    [Fact]
    public async Task Declined_approval_stops_before_any_cost()
    {
        _approve = false;

        var result = await _tools.CallAsync("generate_image", """{"prompt":"kot"}""", alreadyApproved: false);

        Assert.Contains("odrzucił", result);
        Assert.Equal("generate_image", Assert.Single(_confirms).Name);
        Assert.Null(_provider.LastImage);
    }

    [Fact]
    public async Task Approved_chat_calls_do_not_ask_twice()
    {
        var result = await _tools.CallAsync("generate_image", """{"prompt":"kot","width":128,"height":64}""", alreadyApproved: true);

        Assert.Empty(_confirms);
        using var json = JsonDocument.Parse(result);
        Assert.Equal("generate", json.RootElement.GetProperty("Kind").GetString());
        Assert.True(File.Exists(json.RootElement.GetProperty("Path").GetString()));
        Assert.Equal("128x64", _provider.LastImage!.Value.Size);
    }

    [Fact]
    public async Task Preset_fills_omitted_fields_and_kind_must_match()
    {
        await _tools.CallAsync("save_preset", """{"name":"Zestaw","kind":"generate","prompt":"z zestawu","system_prompt":"styl","width":256,"height":256,"seed":5}""", false);

        await _tools.CallAsync("generate_image", """{"preset":"Zestaw","width":512}""", true);

        var (_, prompt, size, options) = _provider.LastImage!.Value;
        Assert.Equal(("styl\n\nz zestawu", "512x256", 5), (prompt, size, options!.Seed));
        await Assert.ThrowsAsync<ArgumentException>(() => _tools.CallAsync("generate_image", """{"prompt":"x","preset":"brak"}""", true));
        Assert.Equal("Zestaw", JsonDocument.Parse(await _tools.CallAsync("list_presets", "{}", false)).RootElement[0].GetProperty("Name").GetString());
    }

    [Fact]
    public async Task Missing_required_arguments_are_reported()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _tools.CallAsync("generate_image", "{}", true));
        await Assert.ThrowsAsync<ArgumentException>(() => _tools.CallAsync("speak", "{}", true));
        await Assert.ThrowsAsync<ArgumentException>(() => _tools.CallAsync("list_audio_models", """{"kind":"video"}""", false));
    }

    [Fact]
    public async Task Gallery_tools_cover_voice_entries_and_need_approval_to_destroy()
    {
        var spoken = await _tools.CallAsync("speak", """{"text":"cześć","format":"wav"}""", true);
        var id = JsonDocument.Parse(spoken).RootElement.GetProperty("Id").GetString()!;

        var listed = JsonDocument.Parse(await _tools.CallAsync("list_gallery", """{"kind":"speech"}""", false)).RootElement;
        Assert.Equal(id, listed[0].GetProperty("Id").GetString());

        _approve = false;
        Assert.Contains("odrzucił", await _tools.CallAsync("delete_gallery_entry", JsonSerializer.Serialize(new { id }), false));
        Assert.Contains("odrzucił", await _tools.CallAsync("clear_gallery", """{"scope":"voice"}""", false));
        Assert.Single(_library.VoiceItems);

        _approve = true;
        await _tools.CallAsync("delete_gallery_entry", JsonSerializer.Serialize(new { id }), false);
        Assert.Empty(_library.VoiceItems);
    }

    [Fact]
    public async Task Composite_routes_calls_by_tool_name_across_sets()
    {
        var control = new AppControlTools(new FakeAppControl(), _ => true);
        var composite = new CompositeToolSet(control, _tools);

        Assert.Contains("get_state", composite.Definitions.Select(d => d.Name));
        Assert.Contains("speak", composite.Definitions.Select(d => d.Name));
        Assert.Equal(composite.AllDefinitions.Count, composite.AllDefinitions.Select(d => d.Name).Distinct().Count());
        Assert.True(composite.IsCostly("generate"));
        Assert.True(composite.IsCostly("generate_image"));
        Assert.False(composite.IsCostly("get_state"));
        Assert.Contains("Gotowy", await composite.CallAsync("get_state", "{}", false));
        Assert.Equal("[]", await composite.CallAsync("list_gallery", "{}", false));
        await Assert.ThrowsAsync<ArgumentException>(() => composite.CallAsync("nie_ma", "{}", false));
    }
}

// Atrapa kontraktu: zapamiętuje żądania zbudowane przez narzędzia, więc test sprawdza samo mapowanie argumentów
internal sealed class RecordingStudio : IStudioApi
{
    public List<object> Requests { get; } = [];
    public IReadOnlyList<string> Kinds { get; set; } = ["generate", "edit", "video", "speech", "transcription", "chat"];

    private static GalleryEntry Entry(string kind) => new() { Kind = kind, Model = "m", FilePath = "C:\\wynik\\1" };

    private Task<StudioResult> Record(object request, string kind)
    {
        Requests.Add(request);
        return Task.FromResult(new StudioResult(Entry(kind)));
    }

    public IReadOnlyList<string> AvailableKinds() => Kinds;

    public DefaultsInfo GetDefaults() => new("fake", "Fake", new Dictionary<string, string?> { ["edit"] = "edit/a" });

    public void SetDefaultModel(string kind, string model) => Requests.Add($"default:{kind}:{model}");
    public Task<StudioResult> GenerateImageAsync(ImageRequest request, CancellationToken ct = default) => Record(request, "generate");
    public Task<StudioResult> EditImageAsync(EditRequest request, CancellationToken ct = default) => Record(request, "edit");
    public Task<StudioResult> GenerateVideoAsync(VideoGenerationRequest request, CancellationToken ct = default) => Record(request, "video");
    public Task<StudioResult> SpeakAsync(SpeechRequest request, CancellationToken ct = default) => Record(request, "speech");
    public Task<StudioResult> TranscribeAsync(TranscriptionRequest request, CancellationToken ct = default) => Record(request, "transcript");

    public Task<IReadOnlyList<ModelSummary>> ModelsAsync(string kind, string? query, int limit, CancellationToken ct = default)
    {
        Requests.Add($"models:{kind}:{query}:{limit}");
        return Task.FromResult<IReadOnlyList<ModelSummary>>([]);
    }

    public Task<IReadOnlyList<AudioModelSummary>> AudioModelsAsync(string kind, string? query, int limit, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<string>> VoicesAsync(string model, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<StudioResult> RepeatAsync(RepeatRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(new StudioResult(Entry("generate")));
    }

    public Task<PromptParts> RebuildPromptAsync(PromptRebuildRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(new PromptParts("s", "c", "zmiana", "n"));
    }

    public Task<string> DescribeImagesAsync(DescribeRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult("opis");
    }

    public IReadOnlyList<Preset> Presets(string? kind) => [];
    public Preset FindPreset(string name) => throw new ArgumentException("brak");

    public Preset SavePreset(SavePresetRequest request)
    {
        Requests.Add(request);
        return new Preset { Name = request.Name };
    }

    public IReadOnlyList<GalleryEntry> List(string? kind, int limit) => [];
    public void RemoveEntry(string id) => Requests.Add($"remove:{id}");

    public Task DeleteEntryAsync(string id, CancellationToken ct = default)
    {
        Requests.Add($"delete:{id}");
        return Task.CompletedTask;
    }

    public void Clear(string scope) => Requests.Add($"clear:{scope}");

    public Task<string> ExportAsync(string id, string target, CancellationToken ct = default)
    {
        Requests.Add($"export:{id}:{target}");
        return Task.FromResult(target);
    }
}

// Kontrakt narzędzi: każde mapuje argumenty JSON na dokładnie jedno żądanie IStudioApi (z domyślnymi wartościami) i nic ponadto
public sealed class StudioToolsContractTests
{
    private readonly RecordingStudio _studio = new();
    private readonly StudioTools _tools;

    public StudioToolsContractTests() => _tools = new StudioTools(_studio, _ => true);

    [Fact]
    public async Task Image_video_and_audio_tools_build_the_expected_requests()
    {
        await _tools.CallAsync("generate_image", """{"prompt":"kot","system_prompt":"styl","model":"m1","width":512,"height":256,"seed":4,"negative_prompt":"bez","quality":"high"}""", true);
        await _tools.CallAsync("generate_image", """{"prompt":"domyślny","quality":"auto"}""", true);
        await _tools.CallAsync("edit_image", """{"prompt":"jaśniej","references":["a.png","https://x.test/b.png"],"width":64,"height":64}""", true);
        await _tools.CallAsync("edit_image", """{"prompt":"bez dopasowania","references":["a.png"]}""", true);
        await _tools.CallAsync("generate_video", """{"prompt":"fala","attachments":["a.png"]}""", true);
        await _tools.CallAsync("generate_video", """{"prompt":"fala","model":"v","duration":9,"aspect_ratio":"1:1"}""", true);
        await _tools.CallAsync("speak", """{"text":"cześć","voice":"nova","speed":1.5,"format":"wav","language":"en"}""", true);
        await _tools.CallAsync("speak", """{"text":"domyślnie"}""", true);
        await _tools.CallAsync("transcribe", """{"path":"a.wav","language":"pl","hint":"Gdańsk"}""", true);

        Assert.Equal(
        [
            new ImageRequest("kot", "styl", "m1", 512, 256, new ImageOptions(4, "bez", "high")),
            new ImageRequest("domyślny", null, null, 1024, 1024, new ImageOptions(null, null, null)),
            new EditRequest("jaśniej", null, ["a.png", "https://x.test/b.png"], null, 64, 64),
            new EditRequest("bez dopasowania", null, ["a.png"], null, null, null),
            new VideoGenerationRequest("fala", null, 5, "16:9", ["a.png"]),
            new VideoGenerationRequest("fala", "v", 9, "1:1", []),
            new SpeechRequest("cześć", null, "nova", 1.5, "wav", "en"),
            new SpeechRequest("domyślnie", null, null, null, "mp3", null),
            new TranscriptionRequest("a.wav", null, "pl", "Gdańsk"),
        ], _studio.Requests, new RequestComparer());
    }

    [Fact]
    public async Task Prompt_description_preset_and_gallery_tools_build_the_expected_requests()
    {
        var rebuilt = await _tools.CallAsync("rebuild_prompt", """{"prompt":"kot","system_prompt":"styl","negative_prompt":"bez","instruction":"ładniej","model":"t"}""", false);
        using var rebuiltJson = JsonDocument.Parse(rebuilt);
        Assert.Equal("s", rebuiltJson.RootElement.GetProperty("system_prompt").GetString());
        Assert.Equal("c", rebuiltJson.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("n", rebuiltJson.RootElement.GetProperty("negative_prompt").GetString());
        Assert.Equal("zmiana", rebuiltJson.RootElement.GetProperty("changes").GetString());
        await _tools.CallAsync("describe_images", """{"images":["a.png"],"model":"vision"}""", false);
        await _tools.CallAsync("save_preset", """{"name":"Z","kind":"edit","prompt":"p","width":640,"height":480,"seed":2}""", false);
        await _tools.CallAsync("remove_gallery_entry", """{"id":"1"}""", false);
        await _tools.CallAsync("delete_gallery_entry", """{"id":"2"}""", true);
        await _tools.CallAsync("clear_gallery", """{"scope":"voice"}""", true);
        await _tools.CallAsync("export_gallery_entry", """{"id":"3","target":"C:\\x.png"}""", true);

        Assert.Equal(
        [
            new PromptRebuildRequest("kot", "styl", "ładniej", "t", NegativePrompt: "bez"),
            new DescribeRequest(["a.png"], "vision", 1536),
            new SavePresetRequest("Z", "edit", "p", null, null, 640, 480, new ImageOptions(2, null, null)),
            "remove:1", "delete:2", "clear:voice", "export:3:C:\\x.png",
        ], _studio.Requests, new RequestComparer());
    }

    [Theory]
    [InlineData("generate_image", """{"prompt":"x","width":10}""")]
    [InlineData("generate_image", """{"prompt":"x","height":9999}""")]
    [InlineData("edit_image", """{"prompt":"x","references":["a"],"width":10}""")]
    [InlineData("save_preset", """{"name":"x","kind":"generate","prompt":"p","width":1}""")]
    public async Task Out_of_range_sizes_never_reach_the_api(string tool, string arguments)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _tools.CallAsync(tool, arguments, true));

        Assert.Empty(_studio.Requests);
    }

    [Fact]
    public void Tools_hide_when_the_api_reports_no_models_for_their_kind()
    {
        _studio.Kinds = ["speech"];

        var visible = _tools.Definitions.Select(d => d.Name).ToList();

        Assert.Contains("speak", visible);
        Assert.DoesNotContain("generate_image", visible);
        Assert.DoesNotContain("describe_images", visible);
        Assert.DoesNotContain("transcribe", visible);
    }

    // Rekordy z listami porównujemy po zawartości (Equals rekordu porównuje listy referencyjnie)
    private sealed class RequestComparer : IEqualityComparer<object>
    {
        public new bool Equals(object? x, object? y) => Text(x) == Text(y);
        public int GetHashCode(object obj) => Text(obj).GetHashCode();
        private static string Text(object? value) => value is string s ? s : System.Text.Json.JsonSerializer.Serialize(value, value!.GetType());
    }
}
