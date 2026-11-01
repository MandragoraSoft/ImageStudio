using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio.Views;

public partial class PromptEditorWindow : Window
{
    private readonly Func<PromptEdit, Task<PromptEdit>>? _rebuild;
    private PromptEdit? _beforeRebuild;
    private IReadOnlyList<PromptRevision>? _history;
    private bool _busy;
    private readonly Action<Preset>? _savePreset;
    private readonly Func<Task<string>>? _describeImages;

    // initial.System == null: rodzaj bez promptu systemowego (np. wideo), sekcja jest ukryta;
    // brak instrukcji albo przebudowy ukrywa sekcję promptu technicznego
    public PromptEditorWindow(string title, PromptEdit initial, Func<PromptEdit, Task<PromptEdit>>? rebuild = null,
                              object? enhancerContext = null,
                              IReadOnlyList<Preset>? presets = null, Action<Preset>? savePreset = null,
                              Func<Task<string>>? describeImages = null)
    {
        InitializeComponent();
        _rebuild = rebuild;
        EnhancerHost.DataContext = enhancerContext;
        EnhancerHost.Visibility = enhancerContext is null ? Visibility.Collapsed : Visibility.Visible;
        Title = title;
        _savePreset = savePreset;
        _describeImages = describeImages;
        DescribeButton.Visibility = describeImages is null ? Visibility.Collapsed : Visibility.Visible;
        PresetBar.Visibility = presets is null ? Visibility.Collapsed : Visibility.Visible;
        PresetBox.ItemsSource = presets;
        PromptHeader.Text = "OCZEKIWANY WYNIK: co ma pokazać generator (temat i scena)";
        SystemSection.Visibility = initial.System is null ? Visibility.Collapsed : Visibility.Visible;
        RebuildSection.Visibility = initial.Instruction is null || rebuild is null ? Visibility.Collapsed : Visibility.Visible;
        SystemBox.Text = initial.System ?? "";
        PromptBox.Text = initial.Prompt;
        _history = initial.History;
        InstructionBox.Text = initial.Instruction ?? "";
        UpdateCounters();
        UpdateHistoryLabel();
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
        RebuildSection.Visibility == Visibility.Visible ? InstructionBox.Text : null,
        _history);

    private void Show(PromptEdit edit)
    {
        if (SystemSection.Visibility == Visibility.Visible) SystemBox.Text = edit.System ?? "";
        PromptBox.Text = edit.Prompt;
        _history = edit.History;
        UpdateHistoryLabel();
    }

    private async void Rebuild_Click(object sender, RoutedEventArgs e)
    {
        if (_rebuild is null || string.IsNullOrWhiteSpace(PromptBox.Text)) return;

        var before = Read();
        SetBusy(true, "Model przebudowuje koncept...");
        try
        {
            var rebuilt = await _rebuild(before);
            Show(rebuilt);
            ChangesStatus.Text = rebuilt.History?.LastOrDefault()?.Changes is { Length: > 0 } changes ? $"Zmiany: {changes}" : "";
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

    // Opis obrazów trafia na koniec konceptu (AppendText zachowuje Ctrl+Z); nic nie jest nadpisywane
    private async void Describe_Click(object sender, RoutedEventArgs e)
    {
        if (_describeImages is null || _busy) return;

        SetBusy(true, "");
        DescribeButton.IsEnabled = false;
        DescribeStatus.Text = "Model opisuje obrazy...";
        try
        {
            var text = await _describeImages();
            if (string.IsNullOrWhiteSpace(text))
            {
                DescribeStatus.Text = "";
                return;
            }

            PromptBox.IsReadOnly = false;
            PromptBox.AppendText((PromptBox.Text.Length > 0 ? "\n\n" : "") + text);
            PromptBox.CaretIndex = PromptBox.Text.Length;
            DescribeStatus.Text = "Dodano opis obrazów.";
        }
        catch (Exception ex)
        {
            DescribeStatus.Text = $"Błąd opisu: {ex.Message}";
        }
        finally
        {
            SetBusy(false, "");
            DescribeButton.IsEnabled = true;
        }
    }

    private void UpdateHistoryLabel()
    {
        var count = _history?.Count ?? 0;
        HistoryLabel.Text = count == 0 ? "Kontekst: brak poprzednich zmian" : $"Kontekst dla modelu: poprzednie zmiany ({count})";
        ClearHistoryButton.IsEnabled = count > 0;
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _history = null;
        ChangesStatus.Text = "";
        UpdateHistoryLabel();
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
        // W trakcie przebudowy wynik nadpisze pola, więc blokujemy wszystko, co je zmienia
        RebuildButton.IsEnabled = PresetBar.IsEnabled = ClearButton.IsEnabled = DescribeButton.IsEnabled = !busy;
        UndoButton.IsEnabled = !busy && _beforeRebuild is not null;
        PromptBox.IsReadOnly = SystemBox.IsReadOnly = InstructionBox.IsReadOnly = busy;
        RebuildStatus.Text = status;
    }

    private void LoadPreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is not Preset preset) return;

        var hasText = !string.IsNullOrWhiteSpace(PromptBox.Text) || !string.IsNullOrWhiteSpace(SystemBox.Text);
        var same = PromptBox.Text == preset.Prompt && SystemBox.Text == preset.SystemPrompt;
        if (hasText && !same && !Confirm($"Zastąpić bieżący prompt w oknie promptem z zestawu „{preset.Name}”?")) return;

        if (SystemSection.Visibility == Visibility.Visible) SystemBox.Text = preset.SystemPrompt;
        PromptBox.Text = preset.Prompt;
        if (RebuildSection.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(preset.EnhancerInstruction))
            InstructionBox.Text = preset.EnhancerInstruction;
    }

    private void Overwrite_Click(object sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is not Preset preset) return;
        if (!Confirm($"Nadpisać prompt w zestawie „{preset.Name}”?\n\nObecna treść zestawu zostanie utracona (model, rozmiar i parametry zostają).")) return;

        preset.Prompt = PromptBox.Text;
        if (SystemSection.Visibility == Visibility.Visible) preset.SystemPrompt = SystemBox.Text;
        if (RebuildSection.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(InstructionBox.Text))
            preset.EnhancerInstruction = InstructionBox.Text;
        try
        {
            _savePreset?.Invoke(preset);
            PresetStatus.Text = $"Zapisano w zestawie „{preset.Name}”.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Nie udało się zapisać zestawu: {ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool Confirm(string text) =>
        MessageBox.Show(this, text, Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void Box_TextChanged(object sender, TextChangedEventArgs e) => UpdateCounters();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SystemBox.Clear();
        PromptBox.Clear();
        _history = null;
        UpdateHistoryLabel();
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
