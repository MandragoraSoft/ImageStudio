using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

public sealed class PromptEditorTests
{
    private static (MainViewModel Vm, FakeDialogs Dialogs) Create()
    {
        var dialogs = new FakeDialogs();
        var vm = TestApp.CreateVm(dialogs: dialogs);
        return (vm, dialogs);
    }

    [Fact]
    public void Editor_result_replaces_system_and_concept_prompts()
    {
        Sta.Run(() =>
        {
            var (vm, dialogs) = Create();
            dialogs.PromptEditResult = new PromptEdit("styl: akwarela", "kot w kapeluszu");

            vm.EditGeneratePromptCommand.Execute(null);

            Assert.Equal("styl: akwarela", vm.GenerateSystemPrompt);
            Assert.Equal("kot w kapeluszu", vm.GeneratePrompt);
        });
    }

    [Fact]
    public void Cancelled_editor_keeps_prompts()
    {
        Sta.Run(() =>
        {
            var (vm, dialogs) = Create();
            vm.VideoPrompt = "fala";
            dialogs.PromptEditResult = null;

            vm.EditVideoPromptCommand.Execute(null);

            Assert.Equal("fala", vm.VideoPrompt);
        });
    }

    [Fact]
    public void Video_editor_changes_only_the_prompt()
    {
        Sta.Run(() =>
        {
            var (vm, dialogs) = Create();
            dialogs.PromptEditResult = new PromptEdit(null, "morze o świcie");

            vm.EditVideoPromptCommand.Execute(null);

            Assert.Equal("morze o świcie", vm.VideoPrompt);
        });
    }

    [Fact]
    public void Rebuild_sends_window_instruction_and_returns_new_parts_without_touching_state()
    {
        Sta.Run(() =>
        {
            var provider = new FakeProvider();
            string? seenInstruction = null;
            provider.Enhance = (instruction, system, concept) =>
            {
                seenInstruction = instruction;
                return new PromptParts("styl: foto", concept + ", złote światło");
            };
            var dialogs = new FakeDialogs();
            var vm = TestApp.CreateVm(provider, dialogs);
            vm.GeneratePrompt = "kot";
            vm.EnhancerModels.Add("m");
            vm.SelectedEnhancerModel = "m";

            vm.EditGeneratePromptCommand.Execute(null);
            var result = dialogs.LastRebuild!(new PromptEdit("", "kot", "dodaj światło")).GetAwaiter().GetResult();

            Assert.Equal("dodaj światło", seenInstruction);
            Assert.Equal("kot, złote światło", result.Prompt);
            Assert.Equal("styl: foto", result.System);
            Assert.Equal("dodaj światło", result.Instruction);
            Assert.Equal("kot", vm.GeneratePrompt);
        });
    }

    [Fact]
    public void Applied_instruction_replaces_enhancer_instruction()
    {
        Sta.Run(() =>
        {
            var (vm, dialogs) = Create();
            dialogs.PromptEditResult = new PromptEdit("", "kot", "moje polecenia");
            vm.EditGeneratePromptCommand.Execute(null);
            Assert.Equal("moje polecenia", vm.EnhancerInstruction);
        });
    }
}
