using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

// Testy zapisujące settings.json i state.json we wspólnym katalogu danych nie mogą biec równolegle z innymi
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UserDataCollection
{
    public const string Name = "Pliki danych użytkownika";
}

// Przepływy głównego okna: zajętość, skróty, zapis ustawień bez zbędnego przeładowania, stany przycisków w oknach zestawów
[Collection(UserDataCollection.Name)]
public sealed class UiFlowTests
{
    private static MainViewModel Create(FakeDialogs dialogs) =>
        new(new FakeProvider(), dialogs, new ImageDownloadService(), AppSettings.Load());

    [Fact]
    public void Busy_change_refreshes_video_and_paste_commands()
    {
        Sta.Run(() =>
        {
            var vm = Create(new FakeDialogs());
            var raised = new HashSet<string>();
            vm.GenerateVideoCommand.CanExecuteChanged += (_, _) => raised.Add("video");
            vm.PasteReferenceCommand.CanExecuteChanged += (_, _) => raised.Add("paste");
            vm.PasteVideoAttachmentCommand.CanExecuteChanged += (_, _) => raised.Add("pasteVideo");
            vm.AddVideoAttachmentsCommand.CanExecuteChanged += (_, _) => raised.Add("addVideo");

            vm.IsBusy = true;

            Assert.Equal(["addVideo", "paste", "pasteVideo", "video"], raised.Order());
            Assert.False(vm.PasteReferenceCommand.CanExecute(null));
        });
    }

    [Fact]
    public void Initialize_ends_not_busy_even_when_models_fail()
    {
        Sta.Run(() =>
        {
            var vm = Create(new FakeDialogs());
            var busySeen = false;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsBusy) && vm.IsBusy) busySeen = true; };

            vm.InitializeAsync().GetAwaiter().GetResult();

            Assert.True(busySeen);
            Assert.False(vm.IsBusy);
        });
    }

    [Fact]
    public void Saving_unchanged_settings_keeps_provider_without_reload()
    {
        Sta.Run(() =>
        {
            var dialogs = new FakeDialogs { SettingsAnswer = current => current };
            var vm = Create(dialogs);

            vm.OpenSettingsCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Equal("fake", vm.ProviderId);
            Assert.Equal("Zapisano ustawienia.", vm.Status);
        });
    }

    [Fact]
    public void Ctrl_enter_runs_action_of_active_image_tab_only()
    {
        Sta.Run(() =>
        {
            var vm = Create(new FakeDialogs());
            vm.GeneratePrompt = "kot";
            vm.SelectedModel = new ModelOption("m", "o", false, ["text"], null);

            vm.ActiveTab = 2; // wideo: skrót nic nie robi
            vm.RunActiveTabCommand.Execute(null);
            Assert.Equal("Gotowy", vm.Status);

            vm.ActiveTab = 0; // FakeProvider nie generuje obrazów, więc próba kończy się błędem w statusie
            vm.RunActiveTabCommand.Execute(null);
            Assert.StartsWith("Błąd", vm.Status);
        });
    }

    [Fact]
    public void Preset_save_requires_name_and_no_pending_suggestion()
    {
        var vm = new PresetSaveViewModel("nazwa");
        Assert.True(vm.CanSave);

        vm.Name = "  ";
        Assert.False(vm.CanSave);

        vm.Name = "x";
        vm.IsSuggesting = true;
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void Preset_details_can_be_saved_only_after_change()
    {
        Sta.Run(() =>
        {
            PresetStore.Create(new Preset { Name = "szczegoly", Kind = "generate", Prompt = "p" });
            var vm = new PresetsViewModel(new FakeDialogs());
            vm.SelectedPreset = vm.Presets.First(p => p.Name == "szczegoly");
            Assert.False(vm.SaveDetailsCommand.CanExecute(null));

            vm.EditDescription = "nowy opis";
            Assert.True(vm.SaveDetailsCommand.CanExecute(null));

            vm.SaveDetailsCommand.Execute(null);
            Assert.False(vm.SaveDetailsCommand.CanExecute(null));
        });
    }
}
