using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using ImageStudio.Configuration;
using ImageStudio.ViewModels;
using ImageStudio.Services;
using Microsoft.Win32;

namespace ImageStudio.Views;

public sealed class WpfDialogService(IPresetStore presets, LegacyPromptImporter importer) : IDialogService
{
    public string[]? PickImageFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Wybierz obrazy referencyjne",
            Filter = "Obrazy|*.png;*.jpg;*.jpeg;*.webp",
            Multiselect = true
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : null;
    }

    public string? ChooseSaveFile(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Zapisz obraz",
            FileName = suggestedFileName,
            Filter = "PNG|*.png|JPEG|*.jpg;*.jpeg|WEBP|*.webp"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void OpenFile(string path) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });

    public string? PickAudioFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Wybierz nagranie",
            Filter = "Nagrania|*.mp3;*.wav;*.ogg;*.flac;*.m4a;*.mp4;*.webm;*.aac|Wszystkie pliki|*.*",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ChooseSaveFileAs(string suggestedFileName, string title, string filter)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = suggestedFileName, Filter = filter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void CopyImage(BitmapSource image) => Clipboard.SetImage(image);

    // Obraz ze schowka (np. zrzut ekranu); null, gdy schowek nie zawiera obrazu
    public BitmapSource? GetClipboardImage() => Clipboard.ContainsImage() ? Clipboard.GetImage() : null;

    // Modalne okno ustawień, właścicielem jest główne okno
    public SettingsResult? ShowSettings(SettingsResult current, IReadOnlyList<ISettingsTab> tabs)
    {
        var viewModel = new SettingsViewModel(current, tabs);
        var window = new SettingsWindow
        {
            DataContext = viewModel,
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow
        };

        window.EnsureTabViews();
        return window.ShowDialog() == true ? viewModel.ToResult() : null;
    }

    public PresetDraft? ShowSavePreset(string defaultName, string prompt = "", Func<Task<PresetDraft>>? suggest = null)
    {
        var viewModel = new PresetSaveViewModel(defaultName, prompt, suggest);
        var window = new PresetSaveWindow { DataContext = viewModel, Owner = OwnerWindow() };
        return window.ShowDialog() == true
            ? new PresetDraft(viewModel.Name.Trim(), viewModel.Description.Trim())
            : null;
    }

    public PromptEdit? ShowPromptEditor(string title, PromptEdit initial, Func<PromptEdit, Task<PromptEdit>>? rebuild = null,
                                       object? enhancerContext = null,
                                       IReadOnlyList<Preset>? presets = null, Action<Preset>? savePreset = null,
                                       Func<Task<string>>? describeImages = null)
    {
        var window = new PromptEditorWindow(title, initial, rebuild, enhancerContext, presets, savePreset, describeImages) { Owner = OwnerWindow() };
        return window.ShowDialog() == true ? window.Result : null;
    }

    public void CopyText(string text) => Clipboard.SetText(text);

    public bool Confirm(string text, string title) =>
        MessageBox.Show(OwnerWindow(), text, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
        == MessageBoxResult.Yes;

    public bool ConfirmToolCall(ToolCall call) =>
        Confirm($"Wywołanie narzędzia (czat albo klient MCP):\n\n{call.Name}\n\nArgumenty:\n{call.ArgumentsJson}\n\nPozwolić?",
                "Wywołanie narzędzia");

    public void ShowModelBrowser(ModelBrowserViewModel viewModel) =>
        new ModelBrowserWindow { DataContext = viewModel, Owner = OwnerWindow() }.ShowDialog();

    public Preset? ShowPresets()
    {
        var viewModel = new PresetsViewModel(this, presets, importer);
        var window = new PresetsWindow { DataContext = viewModel, Owner = OwnerWindow() };
        viewModel.CloseRequested += window.Close;
        window.ShowDialog();
        return viewModel.ChosenPreset;
    }

    private static Window? OwnerWindow() =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
}
