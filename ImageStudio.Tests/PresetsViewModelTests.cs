using ImageStudio.Configuration;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

public sealed class PresetsViewModelTests
{
    private static (TestApp App, PresetsViewModel Vm) Create(string name)
    {
        var app = TestApp.Create();
        app.Presets.Create(new Preset { Name = name, Kind = "generate", Prompt = "p" });
        var vm = app.NewPresetsViewModel();
        vm.SelectedPreset = vm.Presets.First(p => p.Name == name);
        return (app, vm);
    }

    [Fact]
    public void Delete_asks_for_confirmation_and_removes_preset()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Create("do-usuniecia");
            using var _ = app;

            vm.DeletePresetCommand.Execute(null);

            Assert.DoesNotContain(vm.Presets, p => p.Name == "do-usuniecia");
            Assert.DoesNotContain(app.Presets.Presets, p => p.Name == "do-usuniecia");
        });
    }

    [Fact]
    public void Declined_delete_keeps_preset()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Create("zostaje");
            using var _ = app;
            app.Dialogs.ConfirmAnswer = false;

            vm.DeletePresetCommand.Execute(null);

            Assert.Contains(vm.Presets, p => p.Name == "zostaje");
            Assert.Contains(app.Presets.Presets, p => p.Name == "zostaje");
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
            using var app = TestApp.Create();
            var vm = app.Vm;
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.PresetDirty = false;
            var preset = app.Presets.Create(new Preset { Name = "p", Kind = "generate" });
            app.Dialogs.PromptEditResult = new PromptEdit("sys", "koncept");
            app.Dialogs.OverwritePreset = preset;

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
            using var app = TestApp.Create();
            var vm = app.Vm;
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.PresetDirty = false;
            app.Dialogs.PromptEditResult = new PromptEdit("sys", "inny");

            vm.EditGeneratePromptCommand.Execute(null);

            Assert.True(vm.PresetDirty);
        });
    }
}
