using System.IO;
using System.Windows.Media.Imaging;

namespace ImageStudio.Services;

// Obraz dołączony do wiadomości czatu: bajty i typ MIME; Preview to miniatura do widoku (dekodowana przy pierwszym użyciu)
public sealed class ChatImage
{
    private const int PreviewWidth = 320;

    private BitmapSource? _preview;
    private bool _previewTried;

    public ChatImage(byte[] bytes, string mime)
    {
        Bytes = bytes;
        Mime = mime;
    }

    public byte[] Bytes { get; }
    public string Mime { get; }

    // Format wysyłany do API: data URI w polu image_url
    public string DataUri => $"data:{Mime};base64,{Convert.ToBase64String(Bytes)}";

    // null, gdy dekodowanie się nie uda (widok wtedy nic nie pokazuje, a obraz i tak jest wysyłany)
    public BitmapSource? Preview
    {
        get
        {
            if (_previewTried) return _preview;
            _previewTried = true;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = PreviewWidth;
                image.StreamSource = new MemoryStream(Bytes);
                image.EndInit();
                image.Freeze();
                _preview = image;
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException)
            {
                _preview = null;
            }

            return _preview;
        }
    }
}
