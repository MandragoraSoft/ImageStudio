using System.Collections.ObjectModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Configuration;
using ImageStudio.Localization;
using ImageStudio.ViewModels;
using ImageStudio.Views;

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

// Edycja pól zestawu, walidacja, zmiana modelu i dostawcy, niezapisane zmiany, duplikat, szukanie
public sealed class PresetEditingTests
{
    private static ModelOption Model(string id) => new(id, null, false, ["text"], null);

    // Dodatkowe zestawy trafiają do magazynu przed otwarciem okna (lista ładuje się raz, w konstruktorze)
    private static (TestApp App, PresetsViewModel Vm) Open(Preset preset, PresetsContext? context = null, Preset[]? others = null)
    {
        var app = TestApp.Create();
        foreach (var other in others ?? []) app.Presets.Create(other);
        app.Presets.Create(preset);
        var vm = app.NewPresetsViewModel(context);
        vm.SelectedPreset = vm.Presets.First(p => p.Name == preset.Name);
        return (app, vm);
    }

    private static Preset Generate(string name, string prompt = "p") => new() { Name = name, Kind = "generate", Prompt = prompt, Width = 1024, Height = 1024 };

    [Fact]
    public void Editing_prompt_and_size_saves_to_store()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"));
            using var _ = app;

            vm.EditPrompt = "nowy";
            vm.EditWidth = 768;
            vm.SaveDetailsCommand.Execute(null);

            var saved = Assert.Single(app.Presets.Presets);
            Assert.Equal("nowy", saved.Prompt);
            Assert.Equal(768, saved.Width);
        });
    }

    [Fact]
    public void Save_is_disabled_without_changes()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"));
            using var _ = app;

            Assert.False(vm.SaveDetailsCommand.CanExecute(null));

            vm.EditDescription = "opis";

            Assert.True(vm.SaveDetailsCommand.CanExecute(null));
        });
    }

    [Fact]
    public void Empty_name_is_rejected()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"));
            using var _ = app;

            vm.EditName = "   ";
            vm.SaveDetailsCommand.Execute(null);

            Assert.Equal("a", Assert.Single(app.Presets.Presets).Name);
            Assert.NotEmpty(vm.Status);
        });
    }

    [Fact]
    public void Duplicate_name_in_same_kind_is_rejected()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"));
            using var _ = app;
            app.Presets.Create(Generate("Bbb"));

            vm.EditName = "bbb";
            vm.SaveDetailsCommand.Execute(null);

            Assert.Contains(app.Presets.Presets, p => p.Name == "a");
            Assert.NotEmpty(vm.Status);
        });
    }

    [Fact]
    public void Same_name_in_other_kind_is_allowed()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"));
            using var _ = app;
            app.Presets.Create(new Preset { Name = "Bbb", Kind = "edit" });

            vm.EditName = "Bbb";
            vm.SaveDetailsCommand.Execute(null);

            Assert.Equal("Bbb", app.Presets.Presets.First(p => p.Kind == "generate").Name);
        });
    }

    [Fact]
    public void Non_positive_size_is_rejected()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"));
            using var _ = app;

            vm.EditWidth = 0;
            vm.SaveDetailsCommand.Execute(null);

            Assert.Equal(1024, Assert.Single(app.Presets.Presets).Width);
            Assert.NotEmpty(vm.Status);
        });
    }

    [Fact]
    public void Parameters_are_editable()
    {
        Sta.Run(() =>
        {
            var preset = Generate("a");
            preset.Parameters["steps"] = new ParamState { Enabled = false, Value = 2 };
            var (app, vm) = Open(preset);
            using var _ = app;

            vm.EditParameters[0].Value = 5;
            vm.EditParameters[0].Enabled = true;
            vm.SaveDetailsCommand.Execute(null);

            var saved = Assert.Single(app.Presets.Presets).Parameters["steps"];
            Assert.Equal(5, saved.Value);
            Assert.True(saved.Enabled);
        });
    }

    [Fact]
    public void Changing_model_stores_provider()
    {
        Sta.Run(() =>
        {
            var context = new PresetsContext(kind => [Model("a/x"), Model("a/y")], "openrouter");
            var preset = Generate("a");
            preset.Model = "a/x";
            var (app, vm) = Open(preset, context);
            using var _ = app;

            vm.EditModel = "a/y";
            vm.SaveDetailsCommand.Execute(null);

            var saved = Assert.Single(app.Presets.Presets);
            Assert.Equal("a/y", saved.Model);
            Assert.Equal("openrouter", saved.Provider);
        });
    }

    [Fact]
    public void Missing_model_shows_warning_and_is_kept()
    {
        Sta.Run(() =>
        {
            var context = new PresetsContext(kind => [Model("a/x")], "openrouter");
            var preset = Generate("a");
            preset.Model = "stary/model";
            var (app, vm) = Open(preset, context);
            using var _ = app;

            Assert.NotEmpty(vm.ModelWarning);

            vm.EditPrompt = "inny";
            vm.SaveDetailsCommand.Execute(null);

            Assert.Equal("stary/model", Assert.Single(app.Presets.Presets).Model);
        });
    }

    [Fact]
    public void Old_preset_without_provider_keeps_null()
    {
        Sta.Run(() =>
        {
            var context = new PresetsContext(kind => [Model("a/x")], "openrouter");
            var preset = Generate("a");
            preset.Model = "a/x";
            Assert.Null(preset.Provider);
            var (app, vm) = Open(preset, context);
            using var _ = app;

            vm.EditPrompt = "inny";
            vm.SaveDetailsCommand.Execute(null);

            Assert.Null(Assert.Single(app.Presets.Presets).Provider);
        });
    }

    [Fact]
    public void No_context_means_no_warning()
    {
        Sta.Run(() =>
        {
            var preset = Generate("a");
            preset.Model = "stary/model";
            var (app, vm) = Open(preset);
            using var _ = app;

            Assert.Equal("", vm.ModelWarning);
        });
    }

    [Fact]
    public void Switching_with_unsaved_changes_asks_and_saves()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"), others: [Generate("b", "pb")]);
            using var _ = app;
            vm.EditPrompt = "zmiana";
            app.Dialogs.ConfirmAnswer = true;

            vm.SelectedPreset = vm.Presets.First(p => p.Name == "b");

            Assert.Equal("zmiana", app.Presets.Presets.First(p => p.Name == "a").Prompt);
            Assert.Equal("pb", vm.EditPrompt);
        });
    }

    [Fact]
    public void Switching_declined_discards_changes()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"), others: [Generate("b", "pb")]);
            using var _ = app;
            vm.EditPrompt = "zmiana";
            app.Dialogs.ConfirmAnswer = false;

            vm.SelectedPreset = vm.Presets.First(p => p.Name == "b");

            Assert.Equal("p", app.Presets.Presets.First(p => p.Name == "a").Prompt);
            Assert.Equal("pb", vm.EditPrompt);
        });
    }

    [Fact]
    public void Close_without_changes_does_not_ask()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"));
            using var _ = app;

            Assert.True(vm.ConfirmClose());
            Assert.Equal(0, app.Dialogs.ConfirmCount);
        });
    }

    [Fact]
    public void Close_with_changes_and_yes_saves()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"));
            using var _ = app;
            vm.EditPrompt = "zmiana";
            app.Dialogs.ConfirmAnswer = true;

            Assert.True(vm.ConfirmClose());
            Assert.Equal("zmiana", app.Presets.Presets.First(p => p.Name == "a").Prompt);
        });
    }

    [Fact]
    public void Close_with_changes_and_no_discards()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"));
            using var _ = app;
            vm.EditPrompt = "zmiana";
            app.Dialogs.ConfirmAnswer = false;

            Assert.True(vm.ConfirmClose());
            Assert.Equal("p", app.Presets.Presets.First(p => p.Name == "a").Prompt);
        });
    }

    [Fact]
    public void Close_with_invalid_changes_and_yes_stays_open()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"));
            using var _ = app;
            vm.EditName = "";
            vm.EditPrompt = "zmiana";
            app.Dialogs.ConfirmAnswer = true;

            Assert.False(vm.ConfirmClose());
            Assert.Equal("p", app.Presets.Presets.First(p => p.Name == "a").Prompt);
            Assert.Equal("a", app.Presets.Presets.First(p => p.Name == "a").Name);
            Assert.Equal(Loc.T("presets.msg.emptyName"), vm.Status);
        });
    }

    [Fact]
    public void Loading_preset_does_not_ask_twice()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"), others: [Generate("b", "pb")]);
            using var _ = app;
            vm.EditPrompt = "zmiana";
            app.Dialogs.ConfirmAnswer = false;

            vm.LoadPresetCommand.Execute(null);
            Assert.Equal(1, app.Dialogs.ConfirmCount);

            Assert.True(vm.ConfirmClose());
            Assert.Equal(1, app.Dialogs.ConfirmCount);
        });
    }

    [Fact]
    public void Duplicate_creates_copy_with_all_fields()
    {
        Sta.Run(() =>
        {
            var preset = Generate("X", "prompt X");
            preset.Model = "a/x";
            preset.Width = 768;
            preset.Parameters["steps"] = new ParamState { Enabled = true, Value = 4 };
            var (app, vm) = Open(preset);
            using var _ = app;

            vm.DuplicatePresetCommand.Execute(null);

            var copy = app.Presets.Presets.First(p => p.Name == "X (kopia)");
            Assert.Equal("prompt X", copy.Prompt);
            Assert.Equal("a/x", copy.Model);
            Assert.Equal(768, copy.Width);
            Assert.Equal(4, copy.Parameters["steps"].Value);
            Assert.Same(copy, vm.SelectedPreset);
            Assert.Equal("X", app.Presets.Presets.First(p => p.Name == "X").Name);
        });
    }

    [Fact]
    public void Second_duplicate_gets_numbered_name()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("X"));
            using var _ = app;
            var original = vm.SelectedPreset!;
            vm.DuplicatePresetCommand.Execute(null);
            vm.SelectedPreset = original;

            vm.DuplicatePresetCommand.Execute(null);

            Assert.Contains(app.Presets.Presets, p => p.Name == "X (kopia) 2");
        });
    }

    [Fact]
    public void Search_filters_by_name_and_prompt()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("Alfa", "kot na dachu"), others: [Generate("Beta", "pies")]);
            using var _ = app;

            vm.SearchText = "KOT";
            Assert.Equal(["Alfa"], vm.Presets.Select(p => p.Name));

            vm.SearchText = "beta";
            Assert.Equal(["Beta"], vm.Presets.Select(p => p.Name));
        });
    }

    [Fact]
    public void Kind_filter_limits_list()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("G"), others: [new Preset { Name = "E", Kind = "edit" }]);
            using var _ = app;

            vm.KindFilter = "edit";
            Assert.Equal(["E"], vm.Presets.Select(p => p.Name));
        });
    }

    [Fact]
    public void Clearing_search_restores_list()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("G"), others: [Generate("H")]);
            using var _ = app;

            vm.SearchText = "zzz";
            Assert.Empty(vm.Presets);

            vm.SearchText = "";
            Assert.Equal(2, vm.Presets.Count);
        });
    }
}

// Okno zestawów z selektorem modelu (ModelPicker) podpiętym do widoku i filtra z głównego okna
public sealed class PresetsWindowTests
{
    [Fact]
    public void Window_builds_with_model_picker_bound_to_view_model()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var models = new ObservableCollection<ModelOption> { new("a/x", null, false, ["text"], null) };
            var view = new ListCollectionView(models);
            var filter = new ModelFilter(true, () => new ProviderModelPrefs(), new RelayCommand(() => { }));
            var pickers = new Dictionary<string, PresetModelPicker> { ["generate"] = new(view, filter) };
            app.Presets.Create(new Preset { Name = "x", Kind = "generate", Model = "a/x" });
            var vm = app.NewPresetsViewModel(new PresetsContext(_ => models, "fake", pickers));
            vm.SelectedPreset = vm.Presets.First();

            var window = new PresetsWindow { DataContext = vm, ShowInTaskbar = false };
            window.Show();
            window.UpdateLayout();

            Assert.Same(view, vm.PickerView);
            Assert.Same(filter, vm.PickerFilter);
            Assert.Equal("a/x", vm.SelectedModelOption?.Id);
            window.Close();
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
