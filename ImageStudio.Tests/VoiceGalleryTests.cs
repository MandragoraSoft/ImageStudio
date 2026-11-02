using ImageStudio.Storage;
using ImageStudio.Storage.Local;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using ImageStudio.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ImageStudio.Tests;

// Galeria zakładki Głos: wyniki mowy i transkrypcje przeżywają restart, mają menu kontekstowe, łączny koszt i czyszczenie jak galeria obrazów
public sealed class VoiceGalleryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "is-vgal-" + Guid.NewGuid().ToString("N"));

    public VoiceGalleryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string AudioFile(string name = "a.mp3", string? dir = null)
    {
        var path = Path.Combine(dir ?? _dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private GalleryEntry Speech(string text = "dzień dobry", string? file = null, decimal? cost = 0.01m) =>
        new() { Kind = "speech", Prompt = text, Model = "tts/a", CostUsd = cost, FilePath = file ?? AudioFile(Guid.NewGuid().ToString("N") + ".mp3") };

    private TestApp App(MemoryVoiceGalleryStore store, FakeProvider? provider = null)
    {
        provider ??= new FakeProvider();
        provider.SpeechCatalog.Add(new AudioModelInfo("tts/a", "A", new ModelPricing(0.1m, null, null, "1K characters", null, null)));
        provider.TranscriptionCatalog.Add(new AudioModelInfo("stt/a", "A"));
        var app = TestApp.Create(s => s.AddSingleton<IVoiceGalleryStore>(store), provider);
        app.Vm.Voice.OutputDir = _dir;
        app.Get<LocalMediaStorage>().Roots[MediaKind.Audio] = _dir;
        app.Vm.InitializeAsync().GetAwaiter().GetResult();
        return app;
    }

    // ---- trwałość ----

    [Fact]
    public async Task Speech_and_transcripts_are_saved_with_their_details_and_come_back_after_a_restart()
    {
        var store = new MemoryVoiceGalleryStore();
        string speechFile, source;
        using (var app = App(store))
        {
            var fake = app.Get<FakeProvider>();
            fake.OnSynthesize = (_, _, _, _, _) => new AudioResult([9], "mp3", 0.004m);
            fake.OnTranscribe = (_, _, _, _) => new TranscriptResult("tekst z nagrania", 0.001m);
            var voice = app.Vm.Voice;
            voice.SpeechText = "Cześć";
            await voice.SynthesizeCommand.ExecuteAsync(null);
            source = AudioFile("nagranie.wav");
            voice.AudioPath = source;
            await voice.TranscribeCommand.ExecuteAsync(null);
            speechFile = voice.Items[1].Path!;
        }

        Assert.Equal(["transcript", "speech"], store.Entries.Select(e => e.Kind));  // najnowszy pierwszy
        var saved = store.Entries[1];
        Assert.Equal(("Cześć", "tts/a", 0.004m, speechFile), (saved.Prompt, saved.Model, saved.CostUsd, saved.FilePath));
        Assert.Equal(("tekst z nagrania", source), (store.Entries[0].Prompt, store.Entries[0].FilePath));

        using var again = App(store);
        var items = again.Vm.Voice.Items;
        Assert.Equal(2, items.Count);
        Assert.False(items[0].IsSpeech);
        Assert.Equal("tekst z nagrania", items[0].Text);
        Assert.True(items[1].IsSpeech);
        Assert.Equal("Cześć", items[1].Text);
        Assert.Equal(0.004m, items[1].CostUsd);
        Assert.True(items[1].HasAudio);
        Assert.Equal(saved.Id, items[1].Id);
    }

    [Fact]
    public async Task A_translation_note_survives_the_restart()
    {
        var store = new MemoryVoiceGalleryStore();
        using (var app = App(store))
        {
            app.Get<FakeProvider>().ChatCatalog = [new ChatModelInfo("txt/m", [])];
            await app.Vm.InitializeAsync();
            app.Get<FakeProvider>().OnChat = (_, _) => "Hallo";
            app.Get<FakeProvider>().OnSynthesize = (_, _, _, _, _) => new AudioResult([1], "mp3", null);
            app.Vm.Voice.SpeechText = "Hello";
            app.Vm.Voice.SelectedSpeechLanguage = app.Vm.Voice.Languages.Single(l => l.Code == "de");
            await app.Vm.Voice.SynthesizeCommand.ExecuteAsync(null);
        }

        using var again = App(store);
        Assert.Contains("przetłumaczono: Niemiecki", again.Vm.Voice.Items.Single().Footer);
    }

    [Fact]
    public void Entries_whose_audio_file_is_gone_keep_their_text_without_a_play_button_target()
    {
        var store = new MemoryVoiceGalleryStore();
        store.Entries.Add(Speech("zachowany tekst", Path.Combine(_dir, "znikniety.mp3")));

        using var app = App(store);

        var item = app.Vm.Voice.Items.Single();
        Assert.Equal("zachowany tekst", item.Text);
        Assert.False(item.HasAudio);
    }

    [Fact]
    public async Task The_gallery_keeps_at_most_200_entries_newest_first()
    {
        var store = new MemoryVoiceGalleryStore();
        for (var i = 0; i < FileVoiceGalleryStore.MaxEntries; i++) store.Entries.Add(Speech($"stary {i}"));
        using var app = App(store);
        app.Get<FakeProvider>().OnSynthesize = (_, _, _, _, _) => new AudioResult([1], "mp3", null);
        app.Vm.Voice.SpeechText = "najnowszy";

        await app.Vm.Voice.SynthesizeCommand.ExecuteAsync(null);

        Assert.Equal(FileVoiceGalleryStore.MaxEntries, app.Vm.Voice.Items.Count);
        Assert.Equal("najnowszy", app.Vm.Voice.Items[0].Text);
        Assert.Equal("najnowszy", store.Entries[0].Prompt);
        Assert.DoesNotContain(store.Entries, e => e.Prompt == $"stary {FileVoiceGalleryStore.MaxEntries - 1}");
    }

    // ---- usuwanie i czyszczenie ----

    [Fact]
    public void Remove_takes_the_entry_out_of_the_gallery_and_the_store_but_keeps_the_file()
    {
        var store = new MemoryVoiceGalleryStore();
        var keep = Speech("zostaje");
        var drop = Speech("znika");
        store.Entries.AddRange([keep, drop]);
        using var app = App(store);

        app.Vm.Voice.RemoveCommand.Execute(app.Vm.Voice.Items.Single(i => i.Text == "znika"));

        Assert.Equal(["zostaje"], app.Vm.Voice.Items.Select(i => i.Text));
        Assert.Equal(["zostaje"], store.Entries.Select(e => e.Prompt));
        Assert.True(File.Exists(drop.FilePath));
    }

    [Fact]
    public void Delete_with_file_asks_first_and_removes_the_file_only_after_confirmation()
    {
        var store = new MemoryVoiceGalleryStore();
        var entry = Speech("do skasowania");
        store.Entries.Add(entry);
        using var app = App(store);
        var item = app.Vm.Voice.Items.Single();
        var dialogs = app.Get<FakeDialogs>();

        dialogs.ConfirmAnswer = false;
        app.Vm.Voice.DeleteWithFileCommand.Execute(item);
        Assert.True(File.Exists(entry.FilePath));
        Assert.Single(app.Vm.Voice.Items);

        dialogs.ConfirmAnswer = true;
        app.Vm.Voice.DeleteWithFileCommand.Execute(item);
        Assert.False(File.Exists(entry.FilePath));
        Assert.Empty(app.Vm.Voice.Items);
        Assert.Empty(store.Entries);
        Assert.Contains("Usunięto z galerii i z dysku", app.Vm.Status);
    }

    [Fact]
    public void A_locked_file_keeps_the_entry_and_reports_the_problem()
    {
        var store = new MemoryVoiceGalleryStore();
        var entry = Speech();
        store.Entries.Add(entry);
        using var app = App(store);
        app.Get<FakeDialogs>().ConfirmAnswer = true;
        using var locked = new FileStream(entry.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);

        app.Vm.Voice.DeleteWithFileCommand.Execute(app.Vm.Voice.Items.Single());

        Assert.Single(app.Vm.Voice.Items);
        Assert.Contains("Nie udało się usunąć pliku", app.Vm.Status);
    }

    [Fact]
    public void A_file_chosen_by_the_user_outside_the_app_folder_can_never_be_deleted_from_the_gallery()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), "is-vgal-cudze-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MemoryVoiceGalleryStore();
            var entry = new GalleryEntry { Kind = "transcript", Prompt = "moje nagranie", FilePath = AudioFile("wlasne.wav", elsewhere) };
            store.Entries.Add(entry);
            using var app = App(store);
            app.Get<FakeDialogs>().ConfirmAnswer = true;
            var item = app.Vm.Voice.Items.Single();

            Assert.False(app.Vm.Voice.DeleteWithFileCommand.CanExecute(item));
            app.Vm.Voice.DeleteWithFileCommand.Execute(item);

            Assert.True(File.Exists(entry.FilePath));
            Assert.Single(app.Vm.Voice.Items);
        }
        finally
        {
            try { Directory.Delete(elsewhere, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Our_own_recordings_and_speech_files_can_be_deleted()
    {
        var store = new MemoryVoiceGalleryStore();
        store.Entries.Add(new GalleryEntry { Kind = "transcript", Prompt = "t", FilePath = AudioFile("rec.wav", Path.Combine(_dir, "Nagrania")) });
        store.Entries.Add(Speech());
        using var app = App(store);

        Assert.All(app.Vm.Voice.Items, i => Assert.True(app.Vm.Voice.DeleteWithFileCommand.CanExecute(i)));
    }

    [Fact]
    public void Clear_gallery_empties_the_list_and_the_store_but_keeps_the_files()
    {
        var store = new MemoryVoiceGalleryStore();
        var entry = Speech();
        store.Entries.Add(entry);
        using var app = App(store);

        app.Vm.Voice.ClearGalleryCommand.Execute(null);

        Assert.Empty(app.Vm.Voice.Items);
        Assert.Empty(store.Entries);
        Assert.True(File.Exists(entry.FilePath));
        Assert.Contains("Galeria głosu: 0", app.Vm.Voice.GalleryLabel);
    }

    [Fact]
    public void Gallery_label_shows_the_count_and_the_total_cost()
    {
        var store = new MemoryVoiceGalleryStore();
        store.Entries.AddRange([Speech(cost: 0.25m), Speech(cost: 0.5m), Speech(cost: null)]);
        using var app = App(store);

        Assert.Contains("Galeria głosu: 3", app.Vm.Voice.GalleryLabel);
        Assert.Contains("0,7500", app.Vm.Voice.GalleryLabel.Replace('.', ','));
    }

    // ---- widok ----

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    [Fact]
    public void Results_panel_has_the_gallery_header_clear_button_and_a_context_menu_on_each_card()
    {
        WpfHost.Run(() =>
        {
            var store = new MemoryVoiceGalleryStore();
            store.Entries.Add(Speech("tekst karty", cost: 0.02m));
            using var app = App(store);
            app.Vm.ActiveTab = 4;
            var window = app.Get<ImageStudio.MainWindow>();
            window.ShowInTaskbar = false;
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var results = FindAll<VoiceResults>(window).Single();
            Assert.Equal(app.Vm.Voice.GalleryLabel, ((TextBlock)results.FindName("GalleryHeader")).Text);
            Assert.Contains(FindAll<Button>(results), b => b.Content as string == "Wyczyść galerię" && b.IsVisible);

            var card = FindAll<Border>(results).Single(b => b.ContextMenu is not null);
            Assert.Equal(["Powtórz", null, "Usuń z galerii", "Usuń z galerii i z dysku...", null, "Kopiuj tekst"],
                         card.ContextMenu!.Items.Cast<object>().Select(i => (i as MenuItem)?.Header as string));
            Assert.Same(app.Vm.Voice, card.Tag);
            window.Close();
        });
    }
}
