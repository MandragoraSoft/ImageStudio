using ImageStudio.Configuration;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

public sealed class PresetsViewModelTests
{
    private static (PresetsViewModel Vm, FakeDialogs Dialogs) Create(string name)
    {
        PresetStore.Create(new Preset { Name = name, Kind = "generate", Prompt = "p" });
        var dialogs = new FakeDialogs();
        var vm = new PresetsViewModel(dialogs);
        vm.SelectedPreset = vm.Presets.First(p => p.Name == name);
        return (vm, dialogs);
    }

    [Fact]
    public void Delete_asks_for_confirmation_and_removes_preset()
    {
        Sta.Run(() =>
        {
            var (vm, _) = Create("do-usuniecia");
            vm.DeletePresetCommand.Execute(null);
            Assert.DoesNotContain(vm.Presets, p => p.Name == "do-usuniecia");
        });
    }

    [Fact]
    public void Declined_delete_keeps_preset()
    {
        Sta.Run(() =>
        {
            var (vm, dialogs) = Create("zostaje");
            dialogs.ConfirmAnswer = false;
            vm.DeletePresetCommand.Execute(null);
            Assert.Contains(vm.Presets, p => p.Name == "zostaje");
        });
    }
}

public sealed class PromptEditorPresetStateTests
{
    [Fact]
    public void Overwriting_preset_in_editor_keeps_parent_clean()
    {
        Sta.Run(() =>
        {
            var dialogs = new FakeDialogs();
            var vm = new MainViewModel(new FakeProvider(), dialogs, new ImageStudio.Services.ImageDownloadService(), AppSettings.Load());
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.PresetDirty = false;
            var preset = PresetStore.Create(new Preset { Name = "p", Kind = "generate" });
            dialogs.PromptEditResult = new PromptEdit("sys", "koncept");
            dialogs.OverwritePreset = preset;

            vm.EditGeneratePromptCommand.Execute(null);

            Assert.Equal("koncept", preset.Prompt);
            Assert.False(vm.PresetDirty);
        });
    }

    [Fact]
    public void Plain_edit_marks_parent_dirty()
    {
        Sta.Run(() =>
        {
            var dialogs = new FakeDialogs();
            var vm = new MainViewModel(new FakeProvider(), dialogs, new ImageStudio.Services.ImageDownloadService(), AppSettings.Load());
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.PresetDirty = false;
            dialogs.PromptEditResult = new PromptEdit("sys", "inny");

            vm.EditGeneratePromptCommand.Execute(null);

            Assert.True(vm.PresetDirty);
        });
    }
}
