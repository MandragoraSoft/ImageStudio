using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using ImageStudio.Configuration;
using ImageStudio.ViewModels;
using ImageStudio.Views;
using Microsoft.Win32;

namespace ImageStudio.Services;

public sealed class WpfDialogService : IDialogService
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

    public void CopyImage(BitmapSource image) => Clipboard.SetImage(image);

    // Obraz ze schowka (np. zrzut ekranu); null, gdy schowek nie zawiera obrazu
    public BitmapSource? GetClipboardImage() => Clipboard.ContainsImage() ? Clipboard.GetImage() : null;

    // Modalne okno ustawień, właścicielem jest główne okno
    public SettingsResult? ShowSettings(SettingsResult current)
    {
        var viewModel = new SettingsViewModel(current);
        var window = new SettingsWindow
        {
            DataContext = viewModel,
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow
        };

        return window.ShowDialog() == true
            ? new SettingsResult(viewModel.UberLlm.BaseUrl.Trim(), viewModel.UberLlm.MarketUrl.Trim(), viewModel.MaxSide,
                                 viewModel.UberLlm.ApiKey.Trim(), viewModel.SelectedTheme, viewModel.SelectedProvider,
                                 viewModel.OpenRouter.ApiKey.Trim(), viewModel.OpenAi.BaseUrl.Trim(),
                                 viewModel.OpenAi.ApiKey.Trim(),
                                 new Dictionary<string, bool>
                                 {
                                     [viewModel.UberLlm.ProviderId] = viewModel.UberLlm.PreferredOnly,
                                     [viewModel.OpenRouter.ProviderId] = viewModel.OpenRouter.PreferredOnly,
                                     [viewModel.OpenAi.ProviderId] = viewModel.OpenAi.PreferredOnly,
                                 })
            : null;
    }

    public PresetDraft? ShowSavePreset(string defaultName)
    {
        var viewModel = new PresetSaveViewModel(defaultName);
        var window = new PresetSaveWindow { DataContext = viewModel, Owner = OwnerWindow() };
        return window.ShowDialog() == true
            ? new PresetDraft(viewModel.Name.Trim(), viewModel.Description.Trim())
            : null;
    }

    public void ShowModelBrowser(ModelBrowserViewModel viewModel) =>
        new ModelBrowserWindow { DataContext = viewModel, Owner = OwnerWindow() }.ShowDialog();

    public Preset? ShowPresets()
    {
        var viewModel = new PresetsViewModel();
        var window = new PresetsWindow { DataContext = viewModel, Owner = OwnerWindow() };
        viewModel.CloseRequested += window.Close;
        window.ShowDialog();
        return viewModel.ChosenPreset;
    }

    private static Window? OwnerWindow() =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
}
