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

    [Theory]
    [InlineData("generate")]
    [InlineData("edit")]
    public void Loading_a_preset_clears_only_its_own_tab_history(string kind)
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            var history = new[] { new PromptRevision("s", "c", "s2", "c2", "i", "zmiana") };
            app.Dialogs.PromptEditResult = new PromptEdit("s", "c", History: history);
            app.Vm.EditGeneratePromptCommand.Execute(null);
            app.Vm.EditEditPromptCommand.Execute(null);
            app.Dialogs.OnShowPresets = _ => new Preset { Kind = kind, Prompt = "zestaw" };
            app.Vm.OpenPresetsCommand.Execute(null);
            app.Dialogs.PromptEditResult = null;

            app.Vm.EditGeneratePromptCommand.Execute(null);
            Assert.Equal(kind == "generate" ? 0 : 1, app.Dialogs.LastInitial!.History?.Count ?? 0);
            app.Vm.EditEditPromptCommand.Execute(null);
            Assert.Equal(kind == "edit" ? 0 : 1, app.Dialogs.LastInitial!.History?.Count ?? 0);
        });
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
    public void Generation_editor_receives_and_applies_negative_prompt()
    {
        Sta.Run(() =>
        {
            var (vm, dialogs) = Create();
            vm.NegativePrompt = "bez tekstu";
            dialogs.PromptEditResult = new PromptEdit("system", "kot", NegativePrompt: "bez rozmycia");

            vm.EditGeneratePromptCommand.Execute(null);

            Assert.Equal("bez tekstu", dialogs.LastInitial!.NegativePrompt);
            Assert.Equal("bez rozmycia", vm.NegativePrompt);
        });
    }

    [Fact]
    public void Image_edit_editor_uses_its_own_negative_prompt_and_leaves_generation_untouched()
    {
        Sta.Run(() =>
        {
            var (vm, dialogs) = Create();
            vm.NegativePrompt = "zachowaj mnie";
            vm.EditNegativePrompt = "edycja";
            dialogs.PromptEditResult = new PromptEdit("system", "zmieniony", NegativePrompt: "nowa edycja");

            vm.EditEditPromptCommand.Execute(null);

            Assert.Equal("edycja", dialogs.LastInitial!.NegativePrompt);
            Assert.Equal("nowa edycja", vm.EditNegativePrompt);
            Assert.Equal("zachowaj mnie", vm.NegativePrompt);
        });
    }

    [Fact]
    public void Cancelled_editor_keeps_prompts()
    {
        Sta.Run(() =>
        {
            var (vm, dialogs) = Create();
            vm.VideoPrompt = "fala";
            vm.NegativePrompt = "zachowaj negative";
            dialogs.PromptEditResult = null;

            vm.EditVideoPromptCommand.Execute(null);

            Assert.Equal("fala", vm.VideoPrompt);
            Assert.Equal("zachowaj negative", vm.NegativePrompt);
        });
    }

    [Fact]
    public void Video_editor_changes_system_prompt_and_its_own_negative_prompt()
    {
        Sta.Run(() =>
        {
            var (vm, dialogs) = Create();
            vm.NegativePrompt = "generowanie";
            vm.VideoNegativePrompt = "wideo";
            vm.VideoSystemPrompt = "stary system";
            dialogs.PromptEditResult = new PromptEdit("kamera: dron", "morze o świcie", NegativePrompt: "bez ludzi");

            vm.EditVideoPromptCommand.Execute(null);

            Assert.Equal("stary system", dialogs.LastInitial!.System);
            Assert.Equal("wideo", dialogs.LastInitial.NegativePrompt);
            Assert.Equal("kamera: dron", vm.VideoSystemPrompt);
            Assert.Equal("", vm.GenerateSystemPrompt);
            Assert.Equal("morze o świcie", vm.VideoPrompt);
            Assert.Equal("bez ludzi", vm.VideoNegativePrompt);
            Assert.Equal("generowanie", vm.NegativePrompt);
        });
    }

    [Fact]
    public void Video_system_and_negative_prompts_are_persisted_in_the_user_state()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            app.Vm.InitializeAsync().GetAwaiter().GetResult();  // SaveState zapisuje dopiero po inicjalizacji (_ready)
            Assert.Equal("", app.Vm.VideoSystemPrompt);  // starszy state.json bez pól = puste

            app.Vm.VideoSystemPrompt = "kamera: dron";
            app.Vm.VideoNegativePrompt = "bez ludzi";
            app.Vm.EditNegativePrompt = "rozmycie";

            var saved = app.State.State;
            Assert.Equal(("kamera: dron", "bez ludzi", "rozmycie"), (saved.VideoSystemPrompt, saved.VideoNegativePrompt, saved.EditNegativePrompt));
        });
    }

    [Fact]
    public void Cancelled_editor_keeps_edit_and_video_negative_prompts()
    {
        Sta.Run(() =>
        {
            var (vm, dialogs) = Create();
            vm.EditNegativePrompt = "edycja";
            vm.VideoNegativePrompt = "wideo";
            vm.VideoSystemPrompt = "system wideo";
            dialogs.PromptEditResult = null;

            vm.EditEditPromptCommand.Execute(null);
            vm.EditVideoPromptCommand.Execute(null);
            Assert.Equal("system wideo", vm.VideoSystemPrompt);

            Assert.Equal("edycja", vm.EditNegativePrompt);
            Assert.Equal("wideo", vm.VideoNegativePrompt);
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
