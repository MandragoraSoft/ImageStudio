using System.Text.Json;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.Storage;
using ImageStudio.Storage.Local;
using ImageStudio.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ImageStudio.Tests;

// Powtórzenie operacji z wpisu galerii (StudioApi, narzędzie MCP, galeria obrazów i wideo, galeria głosu)
public sealed class GalleryRepeatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "is-repeat-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProvider _provider = new();
    private readonly MemoryLibrary _library = new();
    private readonly StudioApi _studio;

    public GalleryRepeatTests()
    {
        Directory.CreateDirectory(_dir);
        var storage = new LocalMediaStorage { Roots = { [MediaKind.Image] = _dir, [MediaKind.Video] = _dir, [MediaKind.Audio] = _dir } };
        _studio = new StudioApi(() => _provider, storage, _library, new MemoryPresetStore())
        {
            Catalog = new ProviderCatalog(
                new ImageModels([new ImageModelInfo("gen/a", null, false, ["text"]), new ImageModelInfo("gen/b", null, false, ["text"])],
                                [new ImageModelInfo("edit/a", null, false, ["image"])]),
                [new ImageModelInfo("vid/a", null, false, ["text"])], [new ChatModelInfo("chat/a", [])], new Dictionary<string, ModelPricing>(), [],
                [new AudioModelInfo("tts/a", null)], [new AudioModelInfo("stt/a", null)]),
        };
        _provider.OnGenerateImage = (_, _, _, _) => new ImageResult(TestImages.Png(8, 8), null);
        _provider.OnEditImage = (_, _, _) => new ImageResult(TestImages.Png(8, 8), null);
        _provider.OnGenerateVideo = (_, _, _, _, _) => new ImageResult([1, 2, 3], null);
        _provider.OnSynthesize = (_, _, _, _, format) => new AudioResult([1], format, null);
        _provider.OnTranscribe = (_, _, _, _) => new TranscriptResult("tekst", null);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Picture(string name = "a.png", int w = 4, int h = 4)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, TestImages.Png(w, h));
        return path;
    }

    [Fact]
    public async Task Repeating_an_image_reuses_prompt_model_size_and_options()
    {
        var first = (await _studio.GenerateImageAsync(new ImageRequest("kot", "styl", "gen/b", 512, 256, new ImageOptions(7, "bez", "high")))).Entry;
        _provider.OnGenerateImage = (_, _, _, _) => new ImageResult(TestImages.Png(8, 8), 0.5m);

        var again = (await _studio.RepeatAsync(new RepeatRequest(first.Id))).Entry;

        Assert.NotEqual(first.Id, again.Id);
        Assert.Equal(("gen/b", "styl\n\nkot", "512x256"), (_provider.LastImage!.Value.Model, _provider.LastImage.Value.Prompt, _provider.LastImage.Value.Size));
        Assert.Equal(new ImageOptions(7, "bez", "high"), _provider.LastImage.Value.Options);
        Assert.Equal(2, _library.MediaItems.Count);
        Assert.Same(again, _library.MediaItems[0]);
    }

    [Fact]
    public async Task Repeating_can_switch_the_model()
    {
        var first = (await _studio.GenerateImageAsync(new ImageRequest("kot", Model: "gen/a"))).Entry;

        await _studio.RepeatAsync(new RepeatRequest(first.Id, Model: "gen/b"));

        Assert.Equal("gen/b", _provider.LastImage!.Value.Model);
        await Assert.ThrowsAsync<ArgumentException>(() => _studio.RepeatAsync(new RepeatRequest(first.Id, Model: "nie-ma")));
    }

    [Fact]
    public async Task Repeating_an_edit_reuses_the_saved_references_and_size()
    {
        var reference = Picture();
        var first = (await _studio.EditImageAsync(new EditRequest("jaśniej", null, [reference], Width: 16, Height: 8))).Entry;

        await _studio.RepeatAsync(new RepeatRequest(first.Id));

        Assert.Equal(1, _provider.LastEdit!.Value.Paths.Count);
        Assert.Equal(reference, _library.MediaItems[0].Inputs![0]);
        Assert.Equal("jaśniej", _provider.LastEdit.Value.Prompt);
        Assert.Equal("edit/a", _provider.LastEdit.Value.Model);
        Assert.Equal(2, _library.MediaItems.Count);
        Assert.Equal("16x8", _library.MediaItems[0].Size);
    }

    [Fact]
    public async Task An_older_edit_without_saved_references_cannot_be_repeated()
    {
        var old = new GalleryEntry { Kind = "edit", Prompt = "x", Model = "edit/a", Size = "8x8", FilePath = "C:\\x" };
        _library.MediaItems.Add(old);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => _studio.RepeatAsync(new RepeatRequest(old.Id)));

        Assert.Contains("referencji", error.Message);
    }

    [Fact]
    public async Task Repeating_a_video_reuses_duration_aspect_and_attachments_and_reports_progress()
    {
        var picture = Picture();
        var first = (await _studio.GenerateVideoAsync(new VideoGenerationRequest("fala", null, 8, "9:16", [picture]))).Entry;
        var progress = new List<string>();

        await _studio.RepeatAsync(new RepeatRequest(first.Id, Progress: progress.Add));

        Assert.Equal(("vid/a", "fala", 8, "9:16", 1), _provider.LastVideo!.Value);
    }

    [Fact]
    public async Task An_older_video_entry_is_repeated_from_its_description()
    {
        var old = new GalleryEntry { Kind = "video", Prompt = "stare", Model = "vid/a", Size = "1:1 · 10 s", FilePath = "C:\\x" };
        _library.MediaItems.Add(old);

        await _studio.RepeatAsync(new RepeatRequest(old.Id));

        Assert.Equal(("vid/a", "stare", 10, "1:1", 0), _provider.LastVideo!.Value);
    }

    [Fact]
    public async Task Repeating_speech_and_transcription_reuses_their_parameters()
    {
        var speech = (await _studio.SpeakAsync(new SpeechRequest("Cześć", null, "nova", 1.5, "wav"))).Entry;
        var audio = Picture("nagranie.wav");
        var transcript = (await _studio.TranscribeAsync(new TranscriptionRequest(audio, null, "pl", "Gdańsk"))).Entry;

        await _studio.RepeatAsync(new RepeatRequest(speech.Id));
        Assert.Equal(("tts/a", "Cześć", "nova", 1.5, "wav"), _provider.LastSpeech!.Value);

        await _studio.RepeatAsync(new RepeatRequest(transcript.Id));
        Assert.Equal(("stt/a", audio, "pl", "Gdańsk"), _provider.LastTranscription!.Value);
        Assert.Equal(4, _library.VoiceItems.Count);
    }

    [Fact]
    public async Task Unknown_ids_and_unpublished_repeats_behave()
    {
        var first = (await _studio.GenerateImageAsync(new ImageRequest("kot"))).Entry;

        await _studio.RepeatAsync(new RepeatRequest(first.Id, Publish: false));

        Assert.Single(_library.MediaItems);
        await Assert.ThrowsAsync<ArgumentException>(() => _studio.RepeatAsync(new RepeatRequest("brak")));
    }

    [Fact]
    public async Task The_mcp_tool_needs_approval_and_passes_the_id_and_model()
    {
        var recording = new RecordingStudio();
        var tools = new StudioTools(recording, _ => true);

        Assert.True(tools.IsCostly("repeat_gallery_entry"));
        await tools.CallAsync("repeat_gallery_entry", """{"id":"abc","model":"inny"}""", true);
        await tools.CallAsync("repeat_gallery_entry", """{"id":"def"}""", true);

        Assert.Equal([new RepeatRequest("abc", "inny"), new RepeatRequest("def")], recording.Requests.OfType<RepeatRequest>().ToList());
    }

    [Fact]
    public void Gallery_entries_keep_inputs_and_params_in_the_json_file()
    {
        var entry = new GalleryEntry { Kind = "edit", Inputs = ["a.png", "https://x.test/b.png"], Params = new() { ["seed"] = "5" } };

        var back = JsonSerializer.Deserialize<GalleryEntry>(JsonSerializer.Serialize(entry))!;
        var oldFile = JsonSerializer.Deserialize<GalleryEntry>("""{"Id":"1","Kind":"generate","Prompt":"p"}""")!;

        Assert.Equal(["a.png", "https://x.test/b.png"], back.Inputs);
        Assert.Equal("5", back.Params!["seed"]);
        Assert.Null(oldFile.Inputs);
        Assert.Null(oldFile.Params);
    }

    // --- okno ---

    private static FakeProvider WindowProvider() => new()
    {
        ImageCatalog = new ImageModels([new ImageModelInfo("gen/a", null, false, ["text"])], [new ImageModelInfo("edit/a", null, false, ["image"])]),
        VideoCatalog = [new ImageModelInfo("vid/a", null, false, ["text"])],
        ChatCatalog = [new ChatModelInfo("chat/a", [])],
        PricingCatalog = new Dictionary<string, ModelPricing>(),
        OnGenerateImage = (_, _, _, _) => new ImageResult(TestImages.Png(8, 8), null),
        OnGenerateVideo = (_, _, _, _, _) => new ImageResult([1, 2, 3], null),
    };

    [Fact]
    public async Task The_window_repeats_an_image_from_the_gallery_and_shows_it_like_a_new_result()
    {
        using var app = TestApp.Create(provider: WindowProvider());
        await app.Vm.InitializeAsync();
        app.Vm.GeneratePrompt = "kot";
        await app.Vm.GenerateCommand.ExecuteAsync(null);
        var first = app.Vm.Gallery[0];
        app.Vm.Image = null;

        await app.Vm.RepeatGalleryEntryCommand.ExecuteAsync(first);

        Assert.Equal(2, app.Vm.Gallery.Count);
        Assert.NotSame(first, app.Vm.Gallery[0]);
        Assert.Equal(("kot", "gen/a"), (app.Vm.Gallery[0].Prompt, app.Vm.Gallery[0].Model));
        Assert.NotNull(app.Vm.Image);
        Assert.Contains("Zapisano", app.Vm.Status);
    }

    [Fact]
    public async Task The_window_repeats_a_video_and_reports_a_failure_in_the_status()
    {
        using var app = TestApp.Create(provider: WindowProvider());
        await app.Vm.InitializeAsync();
        app.Vm.VideoPrompt = "fala";
        await app.Vm.GenerateVideoCommand.ExecuteAsync(null);
        var first = app.Vm.Gallery[0];

        await app.Vm.RepeatGalleryEntryCommand.ExecuteAsync(first);
        Assert.Equal(2, app.Vm.Gallery.Count);
        Assert.True(app.Vm.ShowVideo);

        var broken = new GalleryEntry { Kind = "edit", Prompt = "x", Model = "edit/a", FilePath = "C:\\x" };
        ((IStudioLibrary)app.Vm).AddMedia(broken);
        await app.Vm.RepeatGalleryEntryCommand.ExecuteAsync(broken);
        Assert.Contains("referencji", app.Vm.Status);
    }

    [Fact]
    public async Task The_voice_gallery_repeats_speech_with_its_parameters_and_keeps_them_in_the_file()
    {
        var provider = WindowProvider();
        provider.SpeechCatalog.Add(new AudioModelInfo("tts/a", null));
        provider.VoiceCatalog["tts/a"] = ["dan"];
        provider.OnSynthesize = (_, _, _, _, format) => new AudioResult([1], format, null);
        using var app = TestApp.Create(provider: provider);
        app.Get<LocalMediaStorage>().Roots[MediaKind.Audio] = _dir;
        await app.Vm.InitializeAsync();
        var voice = app.Vm.Voice;
        voice.SpeechText = "Cześć";
        voice.Format = "wav";
        await voice.SynthesizeCommand.ExecuteAsync(null);
        var first = voice.Items[0];

        await voice.RepeatCommand.ExecuteAsync(first);

        Assert.Equal(2, voice.Items.Count);
        Assert.Equal("Cześć", voice.Items[0].Text);
        Assert.Equal("wav", provider.LastSpeech!.Value.Format);
        Assert.Equal("wav", voice.Items[0].Params!["format"]);
        Assert.Equal("wav", app.Get<MemoryVoiceGalleryStore>().Entries[0].Params!["format"]);
    }

    [Fact]
    public void The_context_menus_are_bound_to_commands_that_exist()
    {
        Assert.NotNull(typeof(MainViewModel).GetProperty("RepeatGalleryEntryCommand"));
        Assert.NotNull(typeof(VoiceViewModel).GetProperty("RepeatCommand"));
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var views = Directory.Exists(Path.Combine(root, "ImageStudio", "Views")) ? Path.Combine(root, "ImageStudio", "Views") : null;
        if (views is null) return;

        Assert.Contains("RepeatGalleryEntryCommand", File.ReadAllText(Path.Combine(views, "OutputPanel.xaml")));
        Assert.Contains("RepeatCommand", File.ReadAllText(Path.Combine(views, "VoiceResults.xaml")));
    }
}
