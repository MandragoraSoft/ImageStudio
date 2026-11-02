using ImageStudio.Storage;
using ImageStudio.Storage.Local;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ImageStudio.Tests;

// Zakładka Głos: modele i głosy z katalogu dostawcy, synteza mowy, rozpoznawanie (plik i mikrofon), lista wyników
public sealed class VoiceViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "is-voice-" + Guid.NewGuid().ToString("N"));

    public VoiceViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static ModelPricing Price(decimal usd, string unit) => new(usd, null, null, unit, null, null);

    private TestApp App(Action<UserState>? state = null, bool withTextModel = true)
    {
        var provider = new FakeProvider();
        if (withTextModel) provider.ChatCatalog = [new ChatModelInfo("txt/m", [])];
        provider.SpeechCatalog.AddRange([new AudioModelInfo("tts/a", "A", Price(0.1m, "1K characters")), new AudioModelInfo("tts/b", "B")]);
        provider.TranscriptionCatalog.Add(new AudioModelInfo("stt/a", "A", Price(0.006m, "minute")));
        provider.VoiceCatalog["tts/a"] = ["dan", "jess"];
        provider.VoiceCatalog["tts/b"] = ["zoe"];
        var memory = new MemoryUserStateStore();
        state?.Invoke(memory.State);
        var app = TestApp.Create(s => s.AddSingleton<IUserStateStore>(memory), provider);
        app.Vm.Voice.OutputDir = _dir;
        app.Get<LocalMediaStorage>().Roots[MediaKind.Audio] = _dir;
        return app;
    }

    private static async Task<TestApp> LoadedAsync(TestApp app)
    {
        // bez Task.Yield: widoki modeli (ICollectionView) wolno zmieniać tylko z wątku, na którym powstały
        await app.Vm.InitializeAsync();
        return app;
    }

    // Zdarzenia sprzętu wracają na wątek UI przez SynchronizationContext.Post, więc test czeka na skutek
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition(), "warunek nie został spełniony w czasie");
    }

    private FakeProvider Fake(TestApp app) => app.Get<FakeProvider>();

    // ---- katalog ----

    [Fact]
    public async Task Catalog_fills_the_model_lists_and_picks_the_first_model_with_its_voices()
    {
        using var app = await LoadedAsync(App());
        var voice = app.Vm.Voice;

        Assert.Equal(["tts/a", "tts/b"], voice.SpeechModels.Select(m => m.Id));
        Assert.Equal("tts/a", voice.SelectedSpeechModel!.Id);
        Assert.Equal(["dan", "jess"], voice.Voices);
        Assert.Equal("dan", voice.SelectedVoice);
        Assert.Equal("stt/a", voice.SelectedTranscriptionModel!.Id);
        Assert.Equal(Price(0.1m, "1K characters"), voice.SelectedSpeechModel.Pricing);
        Assert.Contains(app.Services.GetServices<IMainTab>(), t => t.Title == "Głos");
    }

    [Fact]
    public async Task Saved_choices_return_when_the_models_still_exist()
    {
        using var app = await LoadedAsync(App(s =>
        {
            s.VoiceSpeechModel = "tts/b";
            s.VoiceName = "zoe";
            s.VoiceSpeed = 1.5;
            s.VoiceFormat = "wav";
            s.VoiceLanguage = "pl";
        }));
        var voice = app.Vm.Voice;

        Assert.Equal("tts/b", voice.SelectedSpeechModel!.Id);
        Assert.Equal("zoe", voice.SelectedVoice);
        Assert.Equal(1.5, voice.Speed);
        Assert.Equal("wav", voice.Format);
        Assert.Equal("pl", voice.Language);
    }

    [Fact]
    public async Task Changing_the_model_reloads_its_voices_and_keeps_the_old_voice_only_if_the_new_model_has_it()
    {
        using var app = await LoadedAsync(App());
        var voice = app.Vm.Voice;
        voice.SelectedVoice = "jess";

        voice.SelectedSpeechModel = voice.SpeechModels.Single(m => m.Id == "tts/b");
        await Task.Delay(50);

        Assert.Equal(["zoe"], voice.Voices);
        Assert.Equal("zoe", voice.SelectedVoice);
    }

    [Fact]
    public async Task A_provider_without_audio_leaves_the_tab_empty_and_switching_provider_clears_the_lists()
    {
        using var app = await LoadedAsync(App());
        var voice = app.Vm.Voice;
        Assert.NotEmpty(voice.SpeechModels);

        voice.Reset();

        Assert.Empty(voice.SpeechModels);
        Assert.Empty(voice.TranscriptionModels);
        Assert.Empty(voice.Voices);
        Assert.Null(voice.SelectedSpeechModel);
        Assert.False(voice.SynthesizeCommand.CanExecute(null));
    }

    [Fact]
    public async Task Choices_are_saved_to_the_user_state()
    {
        var app = App();
        await LoadedAsync(app);
        using var _ = app;
        var voice = app.Vm.Voice;

        voice.SelectedSpeechModel = voice.SpeechModels[1];
        voice.Speed = 1.25;
        voice.Language = "en";
        app.Vm.SaveState();

        var state = app.Get<IUserStateStore>().Load();
        Assert.Equal("tts/b", state.VoiceSpeechModel);
        Assert.Equal(1.25, state.VoiceSpeed);
        Assert.Equal("en", state.VoiceLanguage);
        Assert.Equal("Mikrofon testowy", state.VoiceMicrophone);
    }

    [Fact]
    public async Task Chosen_output_device_goes_to_the_player_and_is_remembered()
    {
        var app = App();
        await LoadedAsync(app);
        using var _ = app;
        var voice = app.Vm.Voice;
        var player = app.Get<FakeAudioPlayer>();
        Assert.Equal(["Domyślne", "Głośniki", "Słuchawki"], voice.Speakers.Select(d => d.Name));
        Assert.Equal(-1, player.OutputDevice);

        voice.SelectedSpeaker = voice.Speakers[2];
        app.Vm.SaveState();

        Assert.Equal(1, player.OutputDevice);
        Assert.Equal("Słuchawki", app.Get<IUserStateStore>().Load().VoiceSpeaker);
    }

    [Fact]
    public async Task Remembered_output_device_is_selected_on_start()
    {
        using var app = await LoadedAsync(App(s => s.VoiceSpeaker = "Głośniki"));

        Assert.Equal("Głośniki", app.Vm.Voice.SelectedSpeaker!.Name);
        Assert.Equal(0, app.Get<FakeAudioPlayer>().OutputDevice);
    }

    // ---- synteza ----

    [Fact]
    public async Task Synthesis_needs_a_model_and_text()
    {
        using var app = await LoadedAsync(App());
        var voice = app.Vm.Voice;

        Assert.False(voice.SynthesizeCommand.CanExecute(null));
        voice.SpeechText = "  ";
        Assert.False(voice.SynthesizeCommand.CanExecute(null));
        voice.SpeechText = "Cześć";
        Assert.True(voice.SynthesizeCommand.CanExecute(null));
    }

    [Fact]
    public async Task Synthesis_saves_the_file_adds_a_result_with_the_api_cost_and_plays_it()
    {
        using var app = await LoadedAsync(App());
        Fake(app).OnSynthesize = (_, _, _, _, _) => new AudioResult([1, 2, 3], "mp3", 0.0042m);
        var voice = app.Vm.Voice;
        voice.SpeechText = "Dzień dobry";
        voice.SelectedVoice = "jess";

        await voice.SynthesizeCommand.ExecuteAsync(null);

        Assert.Equal(("tts/a", "Dzień dobry", "jess", (double?)null, "mp3"), Fake(app).LastSpeech);  // prędkość 1 nie jest wysyłana
        var item = voice.Items.Single();
        Assert.True(item.IsSpeech);
        Assert.Equal(0.0042m, item.CostUsd);
        Assert.Equal(_dir, Path.GetDirectoryName(item.Path));
        Assert.EndsWith(".mp3", item.Path);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(item.Path!));
        Assert.Equal([item.Path], app.Get<FakeAudioPlayer>().Played);
        Assert.True(item.IsPlaying);
        Assert.False(voice.IsBusy);
        Assert.Contains("Zapisano nagranie", app.Vm.Status);
    }

    [Fact]
    public async Task Synthesis_passes_the_chosen_speed_and_format_and_estimates_the_cost_when_the_api_gives_none()
    {
        using var app = await LoadedAsync(App());
        Fake(app).OnSynthesize = (_, _, _, _, _) => new AudioResult([1], "wav", null);
        var voice = app.Vm.Voice;
        voice.SpeechText = new string('a', 1500);  // 2 rozpoczęte tysiące znaków
        voice.Speed = 1.5;
        voice.Format = "wav";

        Assert.Contains("0,2", voice.SpeechEstimateLabel.Replace('.', ','));
        await voice.SynthesizeCommand.ExecuteAsync(null);

        Assert.Equal(1.5, Fake(app).LastSpeech!.Value.Speed);
        Assert.Equal("wav", Fake(app).LastSpeech!.Value.Format);
        Assert.Equal(0.2m, voice.Items.Single().CostUsd);
    }

    [Fact]
    public async Task Failed_synthesis_reports_the_error_and_adds_nothing()
    {
        using var app = await LoadedAsync(App());
        Fake(app).OnSynthesize = (_, _, _, _, _) => throw new InvalidOperationException("HTTP 402: brak środków");
        var voice = app.Vm.Voice;
        voice.SpeechText = "tekst";

        await voice.SynthesizeCommand.ExecuteAsync(null);

        Assert.Empty(voice.Items);
        Assert.Contains("brak środków", app.Vm.Status);
        Assert.False(voice.IsBusy);
        Assert.True(voice.SynthesizeCommand.CanExecute(null));
    }

    // ---- język mowy ----

    [Fact]
    public async Task Without_a_chosen_language_the_text_is_read_as_typed_with_no_translation_call()
    {
        using var app = await LoadedAsync(App());
        Fake(app).OnSynthesize = (_, _, _, _, _) => new AudioResult([1], "mp3", null);
        var voice = app.Vm.Voice;
        voice.SpeechText = "Guten Tag";

        await voice.SynthesizeCommand.ExecuteAsync(null);

        Assert.Equal(0, Fake(app).ChatCalls);
        Assert.Equal("Guten Tag", Fake(app).LastSpeech!.Value.Text);
        Assert.Null(voice.SelectedSpeechLanguage!.Code);
    }

    [Fact]
    public async Task A_chosen_language_translates_with_the_text_model_and_reads_the_translation()
    {
        using var app = await LoadedAsync(App());
        Fake(app).OnChat = (_, _) => "  Dzień dobry" + Environment.NewLine;
        Fake(app).OnSynthesize = (_, _, _, _, _) => new AudioResult([1], "mp3", null);
        var voice = app.Vm.Voice;
        voice.SpeechText = "Good morning";
        voice.SelectedSpeechLanguage = voice.Languages.Single(l => l.Code == "pl");

        await voice.SynthesizeCommand.ExecuteAsync(null);

        Assert.Equal("txt/m", Fake(app).LastModel);
        Assert.Equal(["system", "user"], Fake(app).LastMessages.Select(m => m.Role));
        Assert.Contains("into Polish", Fake(app).LastMessages[0].Content);
        Assert.Equal("Good morning", Fake(app).LastMessages[1].Content);
        Assert.Equal("Dzień dobry", Fake(app).LastSpeech!.Value.Text);
        var item = voice.Items.Single();
        Assert.Equal("Dzień dobry", item.Text);
        Assert.Contains("przetłumaczono: Polski", item.Footer);
    }

    [Fact]
    public async Task Translation_is_refused_without_a_text_model_and_nothing_is_synthesized()
    {
        using var app = await LoadedAsync(App(withTextModel: false));
        Fake(app).OnSynthesize = (_, _, _, _, _) => new AudioResult([1], "mp3", null);
        var voice = app.Vm.Voice;
        voice.SpeechText = "Hello";
        voice.SelectedSpeechLanguage = voice.Languages.Single(l => l.Code == "de");

        await voice.SynthesizeCommand.ExecuteAsync(null);

        Assert.Contains("Brak modelu tekstowego", app.Vm.Status);
        Assert.Null(Fake(app).LastSpeech);
        Assert.Empty(voice.Items);
    }

    [Fact]
    public async Task An_empty_translation_stops_before_synthesis()
    {
        using var app = await LoadedAsync(App());
        Fake(app).OnChat = (_, _) => "   ";
        Fake(app).OnSynthesize = (_, _, _, _, _) => new AudioResult([1], "mp3", null);
        var voice = app.Vm.Voice;
        voice.SpeechText = "Hello";
        voice.SelectedSpeechLanguage = voice.Languages.Single(l => l.Code == "fr");

        await voice.SynthesizeCommand.ExecuteAsync(null);

        Assert.Contains("nie zwrócił tłumaczenia", app.Vm.Status);
        Assert.Null(Fake(app).LastSpeech);
    }

    [Fact]
    public async Task Chosen_language_is_remembered()
    {
        var app = App();
        await LoadedAsync(app);
        using var _ = app;
        app.Vm.Voice.SelectedSpeechLanguage = app.Vm.Voice.Languages.Single(l => l.Code == "es");
        app.Vm.SaveState();
        Assert.Equal("es", app.Get<IUserStateStore>().Load().VoiceSpeechLanguage);

        using var again = await LoadedAsync(App(s => s.VoiceSpeechLanguage = "ja"));
        Assert.Equal("ja", again.Vm.Voice.SelectedSpeechLanguage!.Code);
    }

    // ---- rozpoznawanie ----

    private string AudioFile(string name = "nagranie.wav", int bytes = 10)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public async Task Transcription_needs_a_file_and_a_model()
    {
        using var app = await LoadedAsync(App());
        var voice = app.Vm.Voice;
        Assert.False(voice.TranscribeCommand.CanExecute(null));

        voice.AudioPath = AudioFile();

        Assert.True(voice.TranscribeCommand.CanExecute(null));
    }

    [Fact]
    public async Task Picking_a_file_sets_the_path_and_cancelling_the_dialog_keeps_the_old_one()
    {
        using var app = await LoadedAsync(App());
        var voice = app.Vm.Voice;
        var dialogs = app.Get<FakeDialogs>();

        dialogs.AudioPick = AudioFile();
        voice.PickAudioCommand.Execute(null);
        Assert.Equal(dialogs.AudioPick, voice.AudioPath);

        dialogs.AudioPick = null;
        voice.PickAudioCommand.Execute(null);
        Assert.Equal(AudioFile(), voice.AudioPath);
    }

    [Fact]
    public async Task Transcription_sends_language_and_hint_and_lists_the_text()
    {
        using var app = await LoadedAsync(App());
        Fake(app).OnTranscribe = (_, _, _, _) => new TranscriptResult("  to jest tekst ", 0.001m);
        var voice = app.Vm.Voice;
        voice.AudioPath = AudioFile();
        voice.Language = "pl";
        voice.TranscriptionHint = "Kowalski";

        await voice.TranscribeCommand.ExecuteAsync(null);

        Assert.Equal(("stt/a", voice.AudioPath, "pl", "Kowalski"), Fake(app).LastTranscription);
        var item = voice.Items.Single();
        Assert.False(item.IsSpeech);
        Assert.Equal("to jest tekst", item.Text);
        Assert.Equal(0.001m, item.CostUsd);
    }

    [Fact]
    public async Task Missing_or_oversized_files_are_refused_before_any_upload()
    {
        using var app = await LoadedAsync(App());
        Fake(app).OnTranscribe = (_, _, _, _) => throw new InvalidOperationException("nie powinno dojść do API");
        var voice = app.Vm.Voice;

        voice.AudioPath = Path.Combine(_dir, "brak.wav");
        await voice.TranscribeCommand.ExecuteAsync(null);
        Assert.Contains("Nie ma pliku", app.Vm.Status);

        var big = AudioFile("duzy.wav");
        using (var stream = new FileStream(big, FileMode.Open)) stream.SetLength(VoiceViewModel.MaxAudioBytes + 1);
        voice.AudioPath = big;
        await voice.TranscribeCommand.ExecuteAsync(null);
        Assert.Contains("40 MB", app.Vm.Status);
        Assert.Empty(voice.Items);
        Assert.Null(Fake(app).LastTranscription);
    }

    [Fact]
    public async Task Empty_transcript_is_shown_as_a_note_instead_of_a_blank_result()
    {
        using var app = await LoadedAsync(App());
        Fake(app).OnTranscribe = (_, _, _, _) => new TranscriptResult("", null);
        var voice = app.Vm.Voice;
        voice.AudioPath = AudioFile();

        await voice.TranscribeCommand.ExecuteAsync(null);

        Assert.Contains("brak rozpoznanego tekstu", voice.Items.Single().Text);
    }

    // ---- mikrofon ----

    [Fact]
    public async Task Recording_starts_on_the_selected_microphone_shows_the_level_and_hands_the_file_to_transcription()
    {
        using var app = await LoadedAsync(App());
        var recorder = app.Get<FakeAudioRecorder>();
        var voice = app.Vm.Voice;

        await voice.ToggleRecordingCommand.ExecuteAsync(null);
        Assert.True(voice.IsRecording);
        Assert.Equal(0, recorder.StartedDevice);
        Assert.StartsWith(Path.Combine(_dir, "Nagrania"), recorder.StartedPath);
        Assert.EndsWith(".wav", recorder.StartedPath);
        Assert.False(voice.TranscribeCommand.CanExecute(null));  // nie rozpoznajemy w trakcie nagrywania
        Assert.Contains("Zatrzymaj", voice.RecordButtonText);

        recorder.Level(0.6);
        await Until(() => voice.RecordLevel == 0.6);

        await voice.ToggleRecordingCommand.ExecuteAsync(null);
        Assert.True(recorder.Stopped);
        Assert.False(voice.IsRecording);
        Assert.Equal(0, voice.RecordLevel);
        Assert.Equal(recorder.StartedPath, voice.AudioPath);
        Assert.True(voice.TranscribeCommand.CanExecute(null));
    }

    [Fact]
    public async Task Missing_microphone_or_a_blocked_device_gives_a_hint_instead_of_a_crash()
    {
        using var app = await LoadedAsync(App());
        var recorder = app.Get<FakeAudioRecorder>();
        var voice = app.Vm.Voice;

        recorder.StartError = new InvalidOperationException("MmSystem error 2");
        await voice.ToggleRecordingCommand.ExecuteAsync(null);
        Assert.False(voice.IsRecording);
        Assert.Contains("Prywatność", app.Vm.Status);

        voice.SelectedMicrophone = null;
        await voice.ToggleRecordingCommand.ExecuteAsync(null);
        Assert.Contains("Nie znaleziono mikrofonu", app.Vm.Status);
        Assert.False(voice.IsRecording);
    }

    // ---- wyniki ----

    private async Task<(TestApp App, VoiceItem Item)> WithSpeechResultAsync()
    {
        var app = await LoadedAsync(App());
        Fake(app).OnSynthesize = (_, _, _, _, _) => new AudioResult([7, 7], "mp3", null);
        app.Vm.Voice.SpeechText = "tekst do odczytu";
        await app.Vm.Voice.SynthesizeCommand.ExecuteAsync(null);
        return (app, app.Vm.Voice.Items.Single());
    }

    [Fact]
    public async Task Play_marks_the_item_and_it_unmarks_when_playback_ends_or_is_stopped()
    {
        var (app, item) = await WithSpeechResultAsync();
        using var _ = app;
        var player = app.Get<FakeAudioPlayer>();

        player.FinishPlayback();
        await Until(() => !item.IsPlaying);

        app.Vm.Voice.PlayCommand.Execute(item);
        Assert.True(item.IsPlaying);
        Assert.Equal(2, player.Played.Count);

        app.Vm.Voice.StopPlaybackCommand.Execute(null);
        Assert.False(item.IsPlaying);
    }

    [Fact]
    public async Task A_playback_failure_is_reported_with_its_reason_and_the_external_player_opens_the_file()
    {
        var (app, item) = await WithSpeechResultAsync();
        using var _ = app;
        var player = app.Get<FakeAudioPlayer>();
        var dialogs = app.Get<FakeDialogs>();

        player.Fail("Nie znaleziono kodeka");
        await Until(() => app.Vm.Status.Contains("kodeka"));
        Assert.Contains("Otwórz w odtwarzaczu", app.Vm.Status);

        app.Vm.Voice.OpenExternallyCommand.Execute(item);
        Assert.Equal([item.Path], dialogs.Opened);
        Assert.True(player.Stops > 0);  // wbudowany odtwarzacz nie gra równolegle

        dialogs.OpenError = new InvalidOperationException("brak programu");
        app.Vm.Voice.OpenExternallyCommand.Execute(item);
        Assert.Contains("brak programu", app.Vm.Status);
    }

    [Fact]
    public async Task Saving_a_speech_result_copies_the_file_and_saving_a_transcript_writes_text()
    {
        var (app, speech) = await WithSpeechResultAsync();
        using var _ = app;
        var dialogs = app.Get<FakeDialogs>();
        var voice = app.Vm.Voice;

        dialogs.SaveAsAnswer = Path.Combine(_dir, "kopia.mp3");
        voice.SaveAsCommand.Execute(speech);
        Assert.Equal([7, 7], File.ReadAllBytes(dialogs.SaveAsAnswer));

        Fake(app).OnTranscribe = (_, _, _, _) => new TranscriptResult("zapisany tekst", null);
        voice.AudioPath = AudioFile();
        await voice.TranscribeCommand.ExecuteAsync(null);
        dialogs.SaveAsAnswer = Path.Combine(_dir, "tekst.txt");
        voice.SaveAsCommand.Execute(voice.Items[0]);
        Assert.Equal("zapisany tekst", File.ReadAllText(dialogs.SaveAsAnswer));

        dialogs.SaveAsAnswer = null;  // anulowano okno zapisu
        voice.SaveAsCommand.Execute(speech);
    }

    [Fact]
    public async Task Copy_use_as_speech_text_and_remove_work_on_the_item()
    {
        var (app, item) = await WithSpeechResultAsync();
        using var _ = app;
        var voice = app.Vm.Voice;

        voice.CopyTextCommand.Execute(item);
        Assert.Equal("tekst do odczytu", app.Get<FakeDialogs>().CopiedText);

        voice.SpeechText = "";
        voice.UseAsSpeechTextCommand.Execute(item);
        Assert.Equal("tekst do odczytu", voice.SpeechText);

        voice.RemoveCommand.Execute(item);
        Assert.Empty(voice.Items);
    }
}
