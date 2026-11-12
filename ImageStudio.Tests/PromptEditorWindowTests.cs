using System.Windows;
using System.Windows.Controls;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using ImageStudio.Views;

namespace ImageStudio.Tests;

// Układ dwukolumnowy edytora promptu: sekcje ukrywane według argumentów konstruktora, przycisk Nadpisz i wspólny pasek statusu
public sealed class PromptEditorWindowTests
{
    private static List<Preset> Presets() => [new() { Name = "zestaw", Kind = "generate", Prompt = "p", Width = 1024, Height = 1024 }];

    private static T Named<T>(PromptEditorWindow window, string name) => (T)window.FindName(name)!;

    [Fact]
    public void Window_builds_with_all_sections()
    {
        WpfHost.Run(() =>
        {
            var window = new PromptEditorWindow("Edytor", new PromptEdit("system", "koncept", "instrukcja", null),
                edit => Task.FromResult(edit), null, Presets(), _ => { }, () => Task.FromResult(""));

            Assert.Equal(Visibility.Visible, Named<UIElement>(window, "PresetBar").Visibility);
            Assert.Equal(Visibility.Visible, Named<UIElement>(window, "SystemSection").Visibility);
            Assert.Equal(Visibility.Visible, Named<UIElement>(window, "RebuildSection").Visibility);
            Assert.Equal(Visibility.Visible, Named<UIElement>(window, "DescribeButton").Visibility);
            Assert.NotNull(window.FindName("StatusText"));
        });
    }

    [Fact]
    public void Window_builds_minimal()
    {
        WpfHost.Run(() =>
        {
            var window = new PromptEditorWindow("Edytor", new PromptEdit(null, "koncept"));

            Assert.Equal(Visibility.Collapsed, Named<UIElement>(window, "PresetBar").Visibility);
            Assert.Equal(Visibility.Collapsed, Named<UIElement>(window, "SystemSection").Visibility);
            Assert.Equal(Visibility.Collapsed, Named<UIElement>(window, "RebuildSection").Visibility);
            Assert.Equal(Visibility.Collapsed, Named<UIElement>(window, "DescribeButton").Visibility);
        });
    }

    [Fact]
    public void Negative_prompt_is_visible_only_when_supplied_and_is_returned()
    {
        WpfHost.Run(() =>
        {
            var generated = new PromptEditorWindow("Edytor", new PromptEdit("system", "request", NegativePrompt: "unikaj tekstu"));
            Assert.Equal(Visibility.Visible, Named<UIElement>(generated, "NegativeSection").Visibility);
            Assert.Equal("unikaj tekstu", generated.Result.NegativePrompt);

            var edited = new PromptEditorWindow("Edytor", new PromptEdit("system", "request"));
            Assert.Equal(Visibility.Collapsed, Named<UIElement>(edited, "NegativeSection").Visibility);
            Assert.Null(edited.Result.NegativePrompt);
        });
    }

    [Fact]
    public void Rebuild_updates_all_fields_and_undo_restores_fields_and_history()
    {
        WpfHost.Run(() =>
        {
            var oldHistory = new[] { new PromptRevision("a", "b", "c", "d", "i") };
            var initial = new PromptEdit("system", "koncept", "instrukcja", oldHistory, "negatywny");
            var newHistory = oldHistory.Append(new PromptRevision("system", "koncept", "S", "C", "i", null, "negatywny", "N")).ToArray();
            var window = new PromptEditorWindow("Edytor", initial,
                edit => Task.FromResult(edit with { System = "S", Prompt = "C", NegativePrompt = "N", History = newHistory }));

            Named<Button>(window, "RebuildButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(("S", "C", "N"), (window.Result.System, window.Result.Prompt, window.Result.NegativePrompt));
            Assert.Equal(2, window.Result.History!.Count);
            Named<Button>(window, "UndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(("system", "koncept", "negatywny"), (window.Result.System, window.Result.Prompt, window.Result.NegativePrompt));
            Assert.Equal(oldHistory, window.Result.History);
        });
    }

    [Fact]
    public void Failed_rebuild_does_not_change_fields_or_history()
    {
        WpfHost.Run(() =>
        {
            var history = new[] { new PromptRevision("a", "b", "c", "d", "i") };
            var initial = new PromptEdit("system", "koncept", "instrukcja", history, "negatywny");
            var window = new PromptEditorWindow("Edytor", initial,
                _ => Task.FromException<PromptEdit>(new InvalidOperationException("błędny JSON")));
            Named<Button>(window, "RebuildButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(initial, window.Result);
            Assert.Contains("błędny JSON", Named<TextBlock>(window, "StatusText").Text);
        });
    }

    [Fact]
    public void Conversation_proposal_is_applied_only_on_click_and_can_be_undone()
    {
        WpfHost.Run(() =>
        {
            var history = new[] { new PromptRevision("a", "b", "c", "d", "i") };
            var initial = new PromptEdit("system", "koncept", "instrukcja", history, "negatywny");
            var provider = new FakeProvider { OnChat = (_, _) => "Propozycja:\n```json\n{\"system\":\"S\",\"concept\":\"C\",\"negative\":\"N\",\"changes\":\"zmiana\"}\n```" };
            ChatViewModel? chat = null;
            var window = new PromptEditorWindow("Edytor", initial, edit => Task.FromResult(edit),
                createConversation: (_, report) => chat = new ChatViewModel(() => provider, ["m"], report, "") { SelectedModel = "m" });
            chat!.Input = "popraw";
            chat.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Equal(initial, window.Result);
            Named<Button>(window, "UseConversationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(("S", "C", "N"), (window.Result.System, window.Result.Prompt, window.Result.NegativePrompt));
            Assert.Equal(2, window.Result.History!.Count);
            Named<Button>(window, "ChatUndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(initial, window.Result);
        });
    }

    [Fact]
    public void Conversation_keeps_hidden_fields_hidden_and_undo_works_without_rebuild_panel()
    {
        WpfHost.Run(() =>
        {
            var initial = new PromptEdit(null, "przed");
            var provider = new FakeProvider { OnChat = (_, _) => """{"system":"ukryte","concept":"po","negative":"ukryte"}""" };
            ChatViewModel? chat = null;
            var window = new PromptEditorWindow("Edytor", initial,
                createConversation: (_, report) => chat = new ChatViewModel(() => provider, ["m"], report, "") { SelectedModel = "m" });
            chat!.Input = "popraw";
            chat.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Named<Button>(window, "UseConversationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal((null, "po", null), (window.Result.System, window.Result.Prompt, window.Result.NegativePrompt));
            Named<Button>(window, "ChatUndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(initial, window.Result);
        });
    }

    [Fact]
    public void Conversation_is_visible_when_editor_opens()
    {
        WpfHost.Run(() =>
        {
            var provider = new FakeProvider();
            var window = new PromptEditorWindow("Edytor", new PromptEdit("", "tekst", "i", NegativePrompt: ""),
                edit => Task.FromResult(edit),
                createConversation: (_, report) => new ChatViewModel(() => provider, ["m"], report, "") { SelectedModel = "m" });
            window.Show();
            window.UpdateLayout();
            Assert.True(Named<FrameworkElement>(window, "ConversationHost").IsVisible);
            Assert.True(Named<FrameworkElement>(window, "ConversationHost").ActualHeight > 0);
            window.Close();
        });
    }

    [Fact]
    public void Invalid_or_failed_conversation_does_not_change_editor()
    {
        WpfHost.Run(() =>
        {
            var initial = new PromptEdit(null, "koncept");
            var provider = new FakeProvider { OnChat = (_, _) => """{"system":"","concept":"","negative":""}""" };
            ChatViewModel? chat = null;
            var window = new PromptEditorWindow("Edytor", initial,
                createConversation: (_, report) => chat = new ChatViewModel(() => provider, ["m"], report, "") { SelectedModel = "m" });
            chat!.Input = "popraw";
            chat.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Named<Button>(window, "UseConversationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(initial, window.Result);
            Assert.NotEmpty(Named<TextBlock>(window, "StatusText").Text);

            provider.OnChat = (_, _) => throw new InvalidOperationException("błąd");
            chat.Input = "ponów";
            chat.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Null(chat.LastCompletedReply);
            Named<Button>(window, "UseConversationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(initial, window.Result);
        });
    }

    [Fact]
    public void Loading_generate_preset_updates_visible_negative_prompt()
    {
        WpfHost.Run(() =>
        {
            var preset = new Preset { Name = "zestaw", Prompt = "", SystemPrompt = "", NegativePrompt = "unikaj tekstu" };
            var window = new PromptEditorWindow("Edytor", new PromptEdit("", "", NegativePrompt: ""), presets: [preset]);
            Named<ComboBox>(window, "PresetBox").SelectedIndex = 0;

            Named<Button>(window, "LoadPresetButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal("unikaj tekstu", window.Result.NegativePrompt);
        });
    }

    [Fact]
    public void Overwrite_is_disabled_without_selection()
    {
        WpfHost.Run(() =>
        {
            var window = new PromptEditorWindow("Edytor", new PromptEdit(null, "koncept"), presets: Presets(), savePreset: _ => { });
            var presetBox = Named<ComboBox>(window, "PresetBox");
            var overwrite = Named<Button>(window, "OverwriteButton");

            presetBox.SelectedItem = null;
            Assert.False(overwrite.IsEnabled);

            presetBox.SelectedIndex = 0;
            Assert.True(overwrite.IsEnabled);
        });
    }

    // Pole instrukcji wypełnia wolną wysokość prawej kolumny, a przy małym oknie nie schodzi poniżej minimum.
    // Rozmiar ustawiamy po Show: WindowMemory przywraca zapamiętany rozmiar przy SourceInitialized
    [Fact]
    public void Instruction_box_grows_with_window()
    {
        WpfHost.Run(() =>
        {
            var big = new PromptEditorWindow("Edytor", new PromptEdit("system", "koncept", "instrukcja", null),
                edit => Task.FromResult(edit), null, Presets(), _ => { }, () => Task.FromResult(""));
            big.Show();
            big.Width = 1100;
            big.Height = 900;
            big.UpdateLayout();
            var bigHeight = Named<TextBox>(big, "InstructionBox").ActualHeight;
            big.Close();

            var small = new PromptEditorWindow("Edytor", new PromptEdit("system", "koncept", "instrukcja", null),
                edit => Task.FromResult(edit), null, Presets(), _ => { }, () => Task.FromResult(""));
            small.Show();
            small.Width = 760;
            small.Height = 480;
            small.UpdateLayout();
            var smallHeight = Named<TextBox>(small, "InstructionBox").ActualHeight;
            small.Close();

            Assert.True(bigHeight > 160, $"duże okno: {bigHeight}");
            Assert.True(smallHeight >= 160, $"małe okno: {smallHeight}");
        });
    }

    [Fact]
    public void Old_status_blocks_are_gone()
    {
        WpfHost.Run(() =>
        {
            var window = new PromptEditorWindow("Edytor", new PromptEdit("system", "koncept", "instrukcja", null),
                edit => Task.FromResult(edit), null, Presets(), _ => { }, () => Task.FromResult(""));

            Assert.Null(window.FindName("RebuildStatus"));
            Assert.Null(window.FindName("ChangesStatus"));
            Assert.Null(window.FindName("DescribeStatus"));
            Assert.Null(window.FindName("PresetStatus"));
        });
    }
}
