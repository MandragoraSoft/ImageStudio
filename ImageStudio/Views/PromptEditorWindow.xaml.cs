using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio.Views;

public partial class PromptEditorWindow : Window
{
    private readonly Func<PromptEdit, Task<PromptEdit>>? _rebuild;
    private PromptEdit? _beforeRebuild;
    private bool _busy;

    // initial.System == null: rodzaj bez promptu systemowego (np. wideo), sekcja jest ukryta;
    // brak instrukcji albo przebudowy ukrywa sekcję promptu technicznego
    public PromptEditorWindow(string title, PromptEdit initial, Func<PromptEdit, Task<PromptEdit>>? rebuild = null,
                              object? enhancerContext = null)
    {
        InitializeComponent();
        _rebuild = rebuild;
        EnhancerHost.DataContext = enhancerContext;
        EnhancerHost.Visibility = enhancerContext is null ? Visibility.Collapsed : Visibility.Visible;
        Title = title;
        PromptHeader.Text = "Koncept";
        SystemSection.Visibility = initial.System is null ? Visibility.Collapsed : Visibility.Visible;
        RebuildSection.Visibility = initial.Instruction is null || rebuild is null ? Visibility.Collapsed : Visibility.Visible;
        SystemBox.Text = initial.System ?? "";
        PromptBox.Text = initial.Prompt;
        InstructionBox.Text = initial.Instruction ?? "";
        UpdateCounters();
        Loaded += (_, _) => { PromptBox.Focus(); PromptBox.CaretIndex = PromptBox.Text.Length; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && !_busy) { Apply_Click(this, e); e.Handled = true; }
        };
    }

    public PromptEdit Result => Read();

    private PromptEdit Read() => new(
        SystemSection.Visibility == Visibility.Visible ? SystemBox.Text : null,
        PromptBox.Text,
        RebuildSection.Visibility == Visibility.Visible ? InstructionBox.Text : null);

    private void Show(PromptEdit edit)
    {
        if (SystemSection.Visibility == Visibility.Visible) SystemBox.Text = edit.System ?? "";
        PromptBox.Text = edit.Prompt;
    }

    private async void Rebuild_Click(object sender, RoutedEventArgs e)
    {
        if (_rebuild is null || string.IsNullOrWhiteSpace(PromptBox.Text)) return;

        var before = Read();
        SetBusy(true, "Model przebudowuje koncept...");
        try
        {
            Show(await _rebuild(before));
            _beforeRebuild = before;
            UndoButton.IsEnabled = true;
            RebuildStatus.Text = "";
        }
        catch (Exception ex)
        {
            RebuildStatus.Text = $"Błąd przebudowy: {ex.Message}";
        }
        finally
        {
            SetBusy(false, RebuildStatus.Text);
        }
    }

    private void UndoRebuild_Click(object sender, RoutedEventArgs e)
    {
        if (_beforeRebuild is null) return;
        Show(_beforeRebuild);
        _beforeRebuild = null;
        UndoButton.IsEnabled = false;
    }

    private void DefaultInstruction_Click(object sender, RoutedEventArgs e) => InstructionBox.Text = MainViewModel.DefaultInstruction;

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        RebuildButton.IsEnabled = !busy;
        PromptBox.IsReadOnly = SystemBox.IsReadOnly = busy;
        RebuildStatus.Text = status;
    }

    private void Box_TextChanged(object sender, TextChangedEventArgs e) => UpdateCounters();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SystemBox.Clear();
        PromptBox.Clear();
        PromptBox.Focus();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) DialogResult = true;
    }

    private void UpdateCounters()
    {
        if (InstructionCounter is null) return; // TextChanged przy InitializeComponent
        SystemCounter.Text = Describe(SystemBox.Text);
        PromptCounter.Text = Describe(PromptBox.Text);
        InstructionCounter.Text = Describe(InstructionBox.Text);
    }

    private static string Describe(string text) =>
        $"{text.Length} znaków, {text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length} słów";
}
