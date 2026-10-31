using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ImageStudio.Services;

public sealed class WpfDialogService : IDialogService
{
    public string? PickImageFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Wybierz obraz do edycji",
            Filter = "Obrazy|*.png;*.jpg;*.jpeg;*.webp"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
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
}
