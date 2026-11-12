using ImageStudio.Localization;
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
    private readonly ChatViewModel? _conversation;

    // initial.System == null: rodzaj bez promptu systemowego (np. wideo), sekcja jest ukryta;
    // brak instrukcji albo przebudowy ukrywa sekcję promptu technicznego
    public PromptEditorWindow(string title, PromptEdit initial, Func<PromptEdit, Task<PromptEdit>>? rebuild = null,
                              object? enhancerContext = null,
                              IReadOnlyList<Preset>? presets = null, Action<Preset>? savePreset = null,
                              Func<Task<string>>? describeImages = null,
                              Func<Func<PromptEdit>, Action<string, bool>, ChatViewModel>? createConversation = null)
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
        PromptHeader.Text = Loc.T("prompt.header");
        SystemSection.Visibility = initial.System is null ? Visibility.Collapsed : Visibility.Visible;
        NegativeSection.Visibility = initial.NegativePrompt is null ? Visibility.Collapsed : Visibility.Visible;
        RebuildSection.Visibility = initial.Instruction is null || rebuild is null ? Visibility.Collapsed : Visibility.Visible;
        // Bez opisu obrazów i bez przebudowy prawa kolumna jest pusta: zwijamy ją, prompt zajmuje całe okno
        ConversationTab.Visibility = createConversation is null ? Visibility.Collapsed : Visibility.Visible;
        if (describeImages is null && RebuildSection.Visibility != Visibility.Visible && createConversation is null)
        {
            SidebarTabs.Visibility = Visibility.Collapsed;
            BodyGrid.ColumnDefinitions[1].Width = new GridLength(0);
            BodyGrid.ColumnDefinitions[2].Width = new GridLength(0);
        }
        SystemBox.Text = initial.System ?? "";
        PromptBox.Text = initial.Prompt;
        NegativeBox.Text = initial.NegativePrompt ?? "";
        _history = initial.History;
        InstructionBox.Text = initial.Instruction ?? "";
        _conversation = createConversation?.Invoke(Read, SetStatus);
        ConversationHost.DataContext = _conversation;
        if (_conversation is not null) SidebarTabs.SelectedItem = ConversationTab;
        Closed += (_, _) => _conversation?.StopCommand.Execute(null);
        UpdateCounters();
        UpdateHistoryLabel();
        Loaded += (_, _) => { PromptBox.Focus(); PromptBox.CaretIndex = PromptBox.Text.Length; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && !_busy && !ConversationHost.IsKeyboardFocusWithin)
            { Apply_Click(this, e); e.Handled = true; }
        };
    }

    public PromptEdit Result => Read();

    private PromptEdit Read() => new(
        SystemSection.Visibility == Visibility.Visible ? SystemBox.Text : null,
        PromptBox.Text,
        RebuildSection.Visibility == Visibility.Visible ? InstructionBox.Text : null,
        _history,
        NegativeSection.Visibility == Visibility.Visible ? NegativeBox.Text : null);

    private void Show(PromptEdit edit)
    {
        if (SystemSection.Visibility == Visibility.Visible) SystemBox.Text = edit.System ?? "";
        PromptBox.Text = edit.Prompt;
        if (NegativeSection.Visibility == Visibility.Visible) NegativeBox.Text = edit.NegativePrompt ?? "";
        _history = edit.History;
        UpdateHistoryLabel();
    }

    // Jedyny pasek statusu okna (pod edytorem); warning = błąd, pokazany kolorem ostrzeżenia
    internal void SetStatus(string text, bool warning = false)
    {
        StatusText.Text = text;
        if (warning) StatusText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        else StatusText.ClearValue(TextBlock.ForegroundProperty);
    }

    private async void Rebuild_Click(object sender, RoutedEventArgs e)
    {
        if (_rebuild is null || string.IsNullOrWhiteSpace(PromptBox.Text)) return;

        var before = Read();
        SetBusy(true);
        SetStatus(Loc.T("prompt.msg.rewriting"));
        try
        {
            var rebuilt = await _rebuild(before);
            Show(rebuilt);
            _beforeRebuild = before;
            SetStatus(rebuilt.History?.LastOrDefault()?.Changes is { Length: > 0 } changes ? Loc.T("prompt.changes", changes) : "");
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("prompt.msg.rewriteError", ex.Message), warning: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // Opis obrazów trafia na koniec konceptu (AppendText zachowuje Ctrl+Z); nic nie jest nadpisywane
    private async void Describe_Click(object sender, RoutedEventArgs e)
    {
        if (_describeImages is null || _busy) return;

        SetBusy(true);
        SetStatus(Loc.T("prompt.msg.describing"));
        try
        {
            var text = await _describeImages();
            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus("");
                return;
            }

            PromptBox.AppendText((PromptBox.Text.Length > 0 ? "\n\n" : "") + text);
            PromptBox.CaretIndex = PromptBox.Text.Length;
            SetStatus(Loc.T("prompt.msg.described"));
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("prompt.msg.describeError", ex.Message), warning: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void UpdateHistoryLabel()
    {
        var count = _history?.Count ?? 0;
        HistoryLabel.Text = count == 0 ? Loc.T("prompt.context.none") : Loc.T("prompt.context.count", count);
        ClearHistoryButton.IsEnabled = count > 0;
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _history = null;
        SetStatus("");
        UpdateHistoryLabel();
    }

    private void UndoRebuild_Click(object sender, RoutedEventArgs e)
    {
        if (_beforeRebuild is null) return;
        Show(_beforeRebuild);
        _beforeRebuild = null;
        UndoButton.IsEnabled = false;
        ChatUndoButton.IsEnabled = false;
        SetStatus("");
    }

    private void UseConversation_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _conversation is null || _conversation.IsSending) return;
        try
        {
            var content = _conversation.LastCompletedReply ?? throw new InvalidOperationException(Loc.T("prompt.chatNoResult"));
            var marker = content.LastIndexOf("```json", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                var start = marker + "```json".Length;
                var end = content.IndexOf("```", start, StringComparison.Ordinal);
                if (end < 0) throw new InvalidOperationException(Loc.T("prompt.chatNoResult"));
                content = content[start..end].Trim();
            }
            var parts = OpenAiCompat.ParseSplit(content);
            var before = Read();
            var instruction = _conversation.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
            var revision = new PromptRevision(before.System ?? "", before.Prompt, parts.System, parts.Concept,
                                              instruction, parts.Changes, before.NegativePrompt, parts.Negative);
            var history = (before.History ?? []).Append(revision).TakeLast(OpenAiCompat.MaxHistory).ToList();
            Show(before with { System = before.System is null ? null : parts.System, Prompt = parts.Concept,
                               NegativePrompt = before.NegativePrompt is null ? null : parts.Negative, History = history });
            _beforeRebuild = before;
            UndoButton.IsEnabled = true;
            ChatUndoButton.IsEnabled = true;
            SetStatus(parts.Changes is { Length: > 0 } changes ? Loc.T("prompt.changes", changes) : "");
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(Loc.T("prompt.msg.rewriteError", ex.Message), warning: true);
        }
    }

    private void DefaultInstruction_Click(object sender, RoutedEventArgs e) => InstructionBox.Text = MainViewModel.DefaultInstruction;

    // Blokuje pola, które zmienia wynik przebudowy lub opisu; komunikat ustawia wywołujący przez SetStatus
    private void SetBusy(bool busy)
    {
        _busy = busy;
        // W trakcie przebudowy wynik nadpisze pola, więc blokujemy wszystko, co je zmienia
        RebuildButton.IsEnabled = PresetBar.IsEnabled = ClearButton.IsEnabled = DescribeButton.IsEnabled = !busy;
        UndoButton.IsEnabled = ChatUndoButton.IsEnabled = !busy && _beforeRebuild is not null;
        PromptBox.IsReadOnly = SystemBox.IsReadOnly = InstructionBox.IsReadOnly = NegativeBox.IsReadOnly = busy;
    }

    private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        OverwriteButton.IsEnabled = PresetBox.SelectedItem is not null;

    private void LoadPreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is not Preset preset) return;

        var hasText = !string.IsNullOrWhiteSpace(PromptBox.Text) || !string.IsNullOrWhiteSpace(SystemBox.Text)
            || (NegativeSection.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(NegativeBox.Text));
        var same = PromptBox.Text == preset.Prompt && SystemBox.Text == preset.SystemPrompt
            && (NegativeSection.Visibility != Visibility.Visible || NegativeBox.Text == (preset.NegativePrompt ?? ""));
        if (hasText && !same && !Confirm(Loc.T("prompt.confirmReplace", preset.Name))) return;

        if (SystemSection.Visibility == Visibility.Visible) SystemBox.Text = preset.SystemPrompt;
        PromptBox.Text = preset.Prompt;
        if (NegativeSection.Visibility == Visibility.Visible) NegativeBox.Text = preset.NegativePrompt ?? "";
        if (RebuildSection.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(preset.EnhancerInstruction))
            InstructionBox.Text = preset.EnhancerInstruction;
    }

    private void Overwrite_Click(object sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is not Preset preset) return;
        if (!Confirm(Loc.T("prompt.confirmOverwrite", preset.Name))) return;

        preset.Prompt = PromptBox.Text;
        if (SystemSection.Visibility == Visibility.Visible) preset.SystemPrompt = SystemBox.Text;
        if (NegativeSection.Visibility == Visibility.Visible)
            preset.NegativePrompt = string.IsNullOrWhiteSpace(NegativeBox.Text) ? null : NegativeBox.Text;
        if (RebuildSection.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(InstructionBox.Text))
            preset.EnhancerInstruction = InstructionBox.Text;
        try
        {
            _savePreset?.Invoke(preset);
            SetStatus(Loc.T("prompt.msg.saved", preset.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(Loc.T("prompt.msg.saveFailed", ex.Message), warning: true);
        }
    }

    private bool Confirm(string text) =>
        MessageBox.Show(this, text, Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void Box_TextChanged(object sender, TextChangedEventArgs e) => UpdateCounters();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SystemBox.Clear();
        PromptBox.Clear();
        if (NegativeSection.Visibility == Visibility.Visible) NegativeBox.Clear();
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
        NegativeCounter.Text = Describe(NegativeBox.Text);
        InstructionCounter.Text = Describe(InstructionBox.Text);
    }

    private static string Describe(string text) =>
        Loc.T("prompt.counter", text.Length, text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
}
