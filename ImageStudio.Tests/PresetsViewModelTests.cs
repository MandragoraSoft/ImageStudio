using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Configuration;
using ImageStudio.Localization;
using ImageStudio.Services;
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

    // Wszystkie elementy logiczne danego typu (sekcje, przyciski); kontrolki szablonów (ModelPicker) są pomijane
    private static IEnumerable<T> Logical<T>(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in Logical<T>(child)) yield return nested;
        }
    }

    private static PresetModelPicker Picker(ObservableCollection<ModelOption> models) =>
        new(new ListCollectionView(models), new ModelFilter(true, () => new ProviderModelPrefs(), new RelayCommand(() => { })));

    [Fact]
    public void Presets_window_builds_with_assist_sections()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var models = new ObservableCollection<ModelOption> { new("a/x", null, false, ["text"], null) };
            var pickers = new Dictionary<string, PresetModelPicker> { ["generate"] = Picker(models), ["enhancer"] = Picker(models) };
            var context = new PresetsContext(_ => models, "fake", pickers,
                Suggest: _ => Task.FromResult(new PresetDraft("Nazwa", "Opis")),
                EditPrompt: _ => null);
            app.Presets.Create(new Preset { Name = "x", Kind = "generate", Prompt = "p", Model = "a/x" });
            var vm = app.NewPresetsViewModel(context);
            vm.SelectedPreset = vm.Presets.First();

            var window = new PresetsWindow { DataContext = vm, ShowInTaskbar = false };
            window.Show();
            window.UpdateLayout();

            var buttons = Logical<Button>(window).ToList();
            Assert.Equal(Visibility.Visible, buttons.Single(b => ReferenceEquals(b.Command, vm.SuggestNameCommand)).Visibility);
            Assert.Equal(Visibility.Visible, buttons.Single(b => ReferenceEquals(b.Command, vm.SuggestDescriptionCommand)).Visibility);
            Assert.Equal(Visibility.Visible, buttons.Single(b => ReferenceEquals(b.Command, vm.EditInEditorCommand)).Visibility);

            var autoSave = Logical<CheckBox>(window).Single(c => BindingOperations.GetBinding(c, CheckBox.IsCheckedProperty)?.Path.Path == "AutoSave");
            Assert.NotNull(autoSave);

            // Na starcie rozwinięta jest tylko sekcja Prompt; reszta zwinięta
            Assert.True(((Expander)window.FindName("PromptExpander")!).IsExpanded);
            foreach (var name in new[] { "SystemExpander", "NegativeExpander", "DetailsExpander", "AssistExpander" })
                Assert.False(((Expander)window.FindName(name)!).IsExpanded);
            window.Close();
        });
    }

    [Fact]
    public void Presets_window_hides_ai_buttons_without_context()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var models = new ObservableCollection<ModelOption> { new("a/x", null, false, ["text"], null) };
            app.Presets.Create(new Preset { Name = "x", Kind = "generate", Prompt = "p", Model = "a/x" });
            var vm = app.NewPresetsViewModel(new PresetsContext(_ => models, "fake"));
            vm.SelectedPreset = vm.Presets.First();

            var window = new PresetsWindow { DataContext = vm, ShowInTaskbar = false };
            window.Show();
            window.UpdateLayout();

            var buttons = Logical<Button>(window).ToList();
            Assert.Equal(Visibility.Collapsed, buttons.Single(b => ReferenceEquals(b.Command, vm.SuggestNameCommand)).Visibility);
            Assert.Equal(Visibility.Collapsed, buttons.Single(b => ReferenceEquals(b.Command, vm.SuggestDescriptionCommand)).Visibility);
            Assert.Equal(Visibility.Collapsed, buttons.Single(b => ReferenceEquals(b.Command, vm.EditInEditorCommand)).Visibility);
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
            vm.NegativePrompt = "stare";
            vm.PresetDirty = false;
            var preset = app.Presets.Create(new Preset { Name = "p", Kind = "generate", NegativePrompt = "zapisane" });
            app.Dialogs.PromptEditResult = new PromptEdit("sys", "koncept", NegativePrompt: "nowe");
            app.Dialogs.OverwritePreset = preset;

            vm.EditGeneratePromptCommand.Execute(null);

            Assert.Equal("koncept", preset.Prompt);
            Assert.Equal("nowe", preset.NegativePrompt);
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

// Wspomaganie AI nazwy i opisu, model wspomagania, proporcje, autozapis i edytor promptu w oknie zestawów
public sealed class PresetAssistTests
{
    private static ModelOption Model(string id) => new(id, null, false, ["text"], null);

    private static Preset Generate(string name, string prompt = "p") => new() { Name = name, Kind = "generate", Prompt = prompt, Width = 1024, Height = 1024 };

    private static (TestApp App, PresetsViewModel Vm) Open(Preset preset, PresetsContext? context = null)
    {
        var app = TestApp.Create();
        app.Presets.Create(preset);
        var vm = app.NewPresetsViewModel(context);
        vm.SelectedPreset = vm.Presets.First(p => p.Name == preset.Name);
        return (app, vm);
    }

    // Kontekst z modelami tekstowymi wspomagania (enhancers) i opcjonalnymi wywołaniami
    private static PresetsContext Context(Func<PresetSuggestRequest, Task<PresetDraft>>? suggest = null,
                                          IReadOnlyList<ModelOption>? enhancers = null,
                                          Func<PresetPromptEditRequest, PromptEdit?>? edit = null,
                                          bool autoSave = false, Action<bool>? autoSaveChanged = null) =>
        new(kind => kind == "enhancer" ? enhancers ?? [] : [Model("a/x")], "fake", null, suggest, edit, autoSave, autoSaveChanged);

    // Atrapa wspomagania: zapamiętuje żądanie i zwraca stałą propozycję (albo rzuca, gdy Error ustawiony)
    private sealed class SuggestSpy
    {
        public PresetSuggestRequest? Request { get; private set; }
        public Exception? Error { get; set; }

        public Task<PresetDraft> Suggest(PresetSuggestRequest request)
        {
            Request = request;
            return Error is { } error ? Task.FromException<PresetDraft>(error) : Task.FromResult(new PresetDraft("Nowa nazwa", "Nowy opis"));
        }
    }

    [Fact]
    public void Suggest_name_fills_only_name()
    {
        Sta.Run(() =>
        {
            var spy = new SuggestSpy();
            var (app, vm) = Open(Generate("a"), Context(spy.Suggest));
            using var _ = app;
            vm.EditDescription = "stary opis";

            vm.SuggestNameCommand.Execute(null);

            Assert.Equal("Nowa nazwa", vm.EditName);
            Assert.Equal("stary opis", vm.EditDescription);
        });
    }

    [Fact]
    public void Suggest_description_fills_only_description()
    {
        Sta.Run(() =>
        {
            var spy = new SuggestSpy();
            var (app, vm) = Open(Generate("a"), Context(spy.Suggest));
            using var _ = app;

            vm.SuggestDescriptionCommand.Execute(null);

            Assert.Equal("Nowy opis", vm.EditDescription);
            Assert.Equal("a", vm.EditName);
        });
    }

    [Fact]
    public void Suggest_sends_prompt_system_and_preset_model()
    {
        Sta.Run(() =>
        {
            var spy = new SuggestSpy();
            var (app, vm) = Open(Generate("a", "kot"), Context(spy.Suggest));
            using var _ = app;
            vm.EditSystemPrompt = "sys";
            vm.EditEnhancerModel = "a/x";

            vm.SuggestNameCommand.Execute(null);

            Assert.Equal("kot", spy.Request!.Prompt);
            Assert.Equal("sys", spy.Request.SystemPrompt);
            Assert.Equal("a/x", spy.Request.Model);
        });
    }

    [Fact]
    public void Suggest_failure_sets_status_and_clears_busy()
    {
        Sta.Run(() =>
        {
            var spy = new SuggestSpy { Error = new InvalidOperationException("brak sieci") };
            var (app, vm) = Open(Generate("a"), Context(spy.Suggest));
            using var _ = app;

            vm.SuggestNameCommand.Execute(null);

            Assert.NotEmpty(vm.Status);
            Assert.False(vm.IsSuggesting);
            Assert.Equal("a", vm.EditName);
        });
    }

    [Fact]
    public void Suggest_is_unavailable_without_context()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"));
            using var _ = app;

            Assert.False(vm.CanSuggest);
            Assert.False(vm.SuggestNameCommand.CanExecute(null));
            Assert.False(vm.SuggestDescriptionCommand.CanExecute(null));
        });
    }

    [Fact]
    public void Suggest_needs_prompt()
    {
        Sta.Run(() =>
        {
            var spy = new SuggestSpy();
            var (app, vm) = Open(Generate("a"), Context(spy.Suggest));
            using var _ = app;

            vm.EditPrompt = "  ";

            Assert.False(vm.SuggestNameCommand.CanExecute(null));
        });
    }

    [Fact]
    public void Suggested_name_marks_dirty_but_does_not_save()
    {
        Sta.Run(() =>
        {
            var spy = new SuggestSpy();
            var (app, vm) = Open(Generate("a"), Context(spy.Suggest));
            using var _ = app;

            vm.SuggestNameCommand.Execute(null);

            Assert.True(vm.IsDirty);
            Assert.Equal("a", Assert.Single(app.Presets.Presets).Name);
        });
    }

    [Fact]
    public void Preset_suggestion_uses_requested_model_when_available()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            app.Provider.ChatCatalog = [new ChatModelInfo("req/model", []), new ChatModelInfo("sel/model", [])];
            app.Provider.OnChat = (_, _) => "{\"name\": \"N\", \"description\": \"D\"}";
            app.Vm.InitializeAsync().GetAwaiter().GetResult();
            app.Vm.SelectedEnhancerModel = "sel/model";
            PresetsContext? captured = null;
            app.Dialogs.OnShowPresets = context => { captured = context; return null; };
            app.Vm.OpenPresetsCommand.Execute(null);

            var draft = captured!.Suggest!(new PresetSuggestRequest("p", null, "req/model")).GetAwaiter().GetResult();

            Assert.Equal("req/model", app.Provider.LastModel);
            Assert.Equal("N", draft.Name);
        });
    }

    [Fact]
    public void Preset_suggestion_falls_back_to_selected_enhancer_model()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            app.Provider.ChatCatalog = [new ChatModelInfo("sel/model", [])];
            app.Provider.OnChat = (_, _) => "{\"name\": \"N\", \"description\": \"D\"}";
            app.Vm.InitializeAsync().GetAwaiter().GetResult();
            PresetsContext? captured = null;
            app.Dialogs.OnShowPresets = context => { captured = context; return null; };
            app.Vm.OpenPresetsCommand.Execute(null);

            captured!.Suggest!(new PresetSuggestRequest("p", null, "nieznany/model")).GetAwaiter().GetResult();

            Assert.Equal("sel/model", app.Provider.LastModel);
        });
    }

    [Fact]
    public void Changing_enhancer_model_is_saved()
    {
        Sta.Run(() =>
        {
            var preset = Generate("a");
            preset.EnhancerModel = "e/1";
            var (app, vm) = Open(preset, Context(enhancers: [Model("e/1"), Model("e/2")]));
            using var _ = app;

            vm.EditEnhancerModel = "e/2";
            vm.SaveDetailsCommand.Execute(null);

            Assert.Equal("e/2", Assert.Single(app.Presets.Presets).EnhancerModel);
        });
    }

    [Fact]
    public void Missing_enhancer_model_warns_and_is_kept()
    {
        Sta.Run(() =>
        {
            var preset = Generate("a");
            preset.EnhancerModel = "stary/e";
            var (app, vm) = Open(preset, Context(enhancers: [Model("e/1")]));
            using var _ = app;

            Assert.NotEmpty(vm.EnhancerWarning);

            vm.EditPrompt = "inny";
            vm.SaveDetailsCommand.Execute(null);

            Assert.Equal("stary/e", Assert.Single(app.Presets.Presets).EnhancerModel);
        });
    }

    [Fact]
    public void Enhancer_instruction_is_saved()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"), Context(enhancers: [Model("e/1")]));
            using var _ = app;

            vm.EditEnhancerInstruction = "instrukcja";
            vm.SaveDetailsCommand.Execute(null);

            Assert.Equal("instrukcja", Assert.Single(app.Presets.Presets).EnhancerInstruction);
        });
    }

    [Fact]
    public void Selected_aspect_reflects_size()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"));
            using var _ = app;

            Assert.Equal("1:1", vm.SelectedAspect);
        });
    }

    [Fact]
    public void Choosing_aspect_sets_height()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"));
            using var _ = app;

            vm.SelectedAspect = "16:9";

            Assert.Equal(576, vm.EditHeight);
            Assert.Equal("16:9", vm.SelectedAspect);
        });
    }

    [Fact]
    public void Custom_size_has_no_aspect()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"));
            using var _ = app;

            vm.EditWidth = 1000;
            vm.EditHeight = 333;

            Assert.Null(vm.SelectedAspect);
        });
    }

    [Fact]
    public void Details_summary_changes_with_size()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"));
            using var _ = app;
            Assert.Equal("1024 × 1024 · ziarno: losowy · auto", vm.DetailsSummary);

            vm.EditWidth = 768;

            Assert.Equal("768 × 1024 · ziarno: losowy · auto", vm.DetailsSummary);
        });
    }

    [Fact]
    public void Auto_save_switching_saves_without_asking()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            app.Presets.Create(Generate("a"));
            app.Presets.Create(Generate("b", "pb"));
            var vm = app.NewPresetsViewModel(Context(autoSave: true));
            vm.SelectedPreset = vm.Presets.First(p => p.Name == "a");
            vm.EditPrompt = "zmiana";

            vm.SelectedPreset = vm.Presets.First(p => p.Name == "b");

            Assert.Equal("zmiana", app.Presets.Presets.First(p => p.Name == "a").Prompt);
            Assert.Equal(0, app.Dialogs.ConfirmCount);
        });
    }

    [Fact]
    public void Auto_save_close_saves_without_asking()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"), Context(autoSave: true));
            using var _ = app;
            vm.EditPrompt = "zmiana";

            Assert.True(vm.ConfirmClose());
            Assert.Equal("zmiana", app.Presets.Presets.First(p => p.Name == "a").Prompt);
            Assert.Equal(0, app.Dialogs.ConfirmCount);
        });
    }

    [Fact]
    public void Auto_save_invalid_close_stays_open()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"), Context(autoSave: true));
            using var _ = app;
            vm.EditName = "";
            vm.EditPrompt = "zmiana";

            Assert.False(vm.ConfirmClose());
            Assert.Equal("p", app.Presets.Presets.First(p => p.Name == "a").Prompt);
            Assert.Equal(Loc.T("presets.msg.emptyName"), vm.Status);
            Assert.Equal(0, app.Dialogs.ConfirmCount);
        });
    }

    [Fact]
    public void Auto_save_off_still_asks()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"), Context(autoSave: false));
            using var _ = app;
            vm.EditPrompt = "zmiana";
            app.Dialogs.ConfirmAnswer = false;

            Assert.True(vm.ConfirmClose());
            Assert.Equal(1, app.Dialogs.ConfirmCount);
        });
    }

    [Fact]
    public void Auto_save_change_is_reported_to_context()
    {
        Sta.Run(() =>
        {
            bool? reported = null;
            var (app, vm) = Open(Generate("a"), Context(autoSaveChanged: value => reported = value));
            using var _ = app;

            vm.AutoSave = true;

            Assert.True(reported);
        });
    }

    [Fact]
    public void Auto_save_is_persisted_in_state()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            app.Vm.InitializeAsync().GetAwaiter().GetResult();
            PresetsContext? captured = null;
            app.Dialogs.OnShowPresets = context => { captured = context; return null; };
            app.Vm.OpenPresetsCommand.Execute(null);

            captured!.AutoSaveChanged!(true);

            Assert.True(app.State.State.PresetsAutoSave);
        });
    }

    [Fact]
    public void Auto_save_is_read_from_state_when_opening()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            app.State.State.PresetsAutoSave = true;
            app.Vm.InitializeAsync().GetAwaiter().GetResult();
            PresetsContext? captured = null;
            app.Dialogs.OnShowPresets = context => { captured = context; return null; };

            app.Vm.OpenPresetsCommand.Execute(null);

            Assert.True(captured!.AutoSave);
        });
    }

    [Fact]
    public void Edit_in_editor_writes_result_to_fields()
    {
        Sta.Run(() =>
        {
            var (app, vm) = Open(Generate("a"), Context(edit: _ => new PromptEdit("sys2", "nowy", "instr2")));
            using var _ = app;

            vm.EditInEditorCommand.Execute(null);

            Assert.Equal("nowy", vm.EditPrompt);
            Assert.Equal("sys2", vm.EditSystemPrompt);
            Assert.Equal("instr2", vm.EditEnhancerInstruction);
            Assert.Equal("p", app.Presets.Presets.First(p => p.Name == "a").Prompt);
        });
    }

    [Fact]
    public void Edit_in_editor_cancel_changes_nothing()
    {
        Sta.Run(() =>
        {
            var preset = Generate("a");
            preset.NegativePrompt = "zachowaj";
            var (app, vm) = Open(preset, Context(edit: _ => null));
            using var _ = app;

            vm.EditInEditorCommand.Execute(null);

            Assert.Equal("p", vm.EditPrompt);
            Assert.Equal("zachowaj", vm.EditNegativePrompt);
            Assert.False(vm.IsDirty);
        });
    }

    [Fact]
    public void Generate_preset_editor_loads_and_updates_negative_prompt()
    {
        Sta.Run(() =>
        {
            PresetPromptEditRequest? request = null;
            var preset = Generate("a");
            preset.NegativePrompt = "stare";
            var (app, vm) = Open(preset, Context(edit: r =>
            {
                request = r;
                return r.Initial with { NegativePrompt = "nowe" };
            }));
            using var _ = app;

            vm.EditInEditorCommand.Execute(null);

            Assert.Equal("stare", request!.Initial.NegativePrompt);
            Assert.Equal("nowe", vm.EditNegativePrompt);
        });
    }

    [Fact]
    public void Edit_preset_editor_loads_and_updates_negative_prompt()
    {
        Sta.Run(() =>
        {
            PresetPromptEditRequest? request = null;
            var preset = new Preset { Name = "a", Kind = "edit", Prompt = "p", NegativePrompt = "zachowaj", Width = 1024, Height = 1024 };
            var (app, vm) = Open(preset, Context(edit: r =>
            {
                request = r;
                return r.Initial with { Prompt = "zmieniony", NegativePrompt = "nowe" };
            }));
            using var _ = app;

            vm.EditInEditorCommand.Execute(null);

            Assert.Equal("zachowaj", request!.Initial.NegativePrompt);
            Assert.Equal("nowe", vm.EditNegativePrompt);
        });
    }

    [Fact]
    public void Edit_in_editor_passes_preset_model_and_parameters()
    {
        Sta.Run(() =>
        {
            PresetPromptEditRequest? request = null;
            var preset = Generate("a");
            preset.Parameters["steps"] = new ParamState { Enabled = true, Value = 5 };
            var (app, vm) = Open(preset, Context(edit: r => { request = r; return null; }));
            using var _ = app;
            vm.EditEnhancerModel = "e/1";

            vm.EditInEditorCommand.Execute(null);

            Assert.Equal("e/1", request!.Model);
            Assert.Equal(Loc.T("presets.editor.title", "a"), request.Title);
            Assert.Equal(5, request.Parameters["steps"].Value);
            Assert.True(request.Parameters["steps"].Enabled);
        });
    }

    [Fact]
    public void Editor_from_presets_does_not_touch_main_prompts()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            app.Vm.InitializeAsync().GetAwaiter().GetResult();
            app.Vm.GeneratePrompt = "glowny";
            var dirty = app.Vm.PresetDirty;
            PresetsContext? captured = null;
            app.Dialogs.OnShowPresets = context => { captured = context; return null; };
            app.Vm.OpenPresetsCommand.Execute(null);
            app.Dialogs.PromptEditResult = new PromptEdit("sys", "z edytora");

            var result = captured!.EditPrompt!(new PresetPromptEditRequest("t", new PromptEdit("", "p", null, []), null,
                                                                          new Dictionary<string, ParamState>()));

            Assert.Equal("z edytora", result!.Prompt);
            Assert.Equal("glowny", app.Vm.GeneratePrompt);
            Assert.Equal(dirty, app.Vm.PresetDirty);
        });
    }

    [Fact]
    public void Preset_editor_rebuild_uses_all_fields_without_touching_main_prompts()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            app.Vm.InitializeAsync().GetAwaiter().GetResult();
            app.Vm.EnhancerModels.Add("test-rebuild");
            app.Vm.SelectedEnhancerModel = "test-rebuild";
            app.Vm.GeneratePrompt = "główny";
            app.Provider.EnhanceWithNegative = (_, system, concept, negative) =>
                new PromptParts(system + "+", concept + "+", "zmiana", negative + "+");
            PresetsContext? context = null;
            app.Dialogs.OnShowPresets = c => { context = c; return null; };
            app.Vm.OpenPresetsCommand.Execute(null);
            var initial = new PromptEdit("s", "c", "i", [], "n");
            var request = new PresetPromptEditRequest("t", initial, "test-rebuild", new Dictionary<string, ParamState>());
            context!.EditPrompt!(request);
            var rebuilt = app.Dialogs.LastRebuild!(initial).GetAwaiter().GetResult();
            app.Dialogs.PromptEditResult = rebuilt;
            var result = context.EditPrompt(request);

            Assert.Equal(("s+", "c+", "n+"), (result!.System, result.Prompt, result.NegativePrompt));
            Assert.Equal("n", app.Provider.LastEnhanceNegative);
            Assert.Equal("główny", app.Vm.GeneratePrompt);
        });
    }
}
