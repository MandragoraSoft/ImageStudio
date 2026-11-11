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
