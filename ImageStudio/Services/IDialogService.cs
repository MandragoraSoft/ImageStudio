using System.Windows.Media.Imaging;

namespace ImageStudio.Services;

// Interakcje z systemem, których ViewModel nie wykonuje sam (okna, schowek)
public interface IDialogService
{
    string? PickImageFile();
    string? ChooseSaveFile(string suggestedFileName);
    void CopyImage(BitmapSource image);
}
