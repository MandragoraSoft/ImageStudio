using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageStudio.Services;

// Dopasowuje wynik do wybranych proporcji W:H: przycięcie środkiem, potem skalowanie do W×H.
// Używane przy edycji, bo endpoint edycji nie respektuje parametru rozmiaru.
public static class ImageFitter
{
    // Zmniejsza obraz tak, by dłuższy bok nie przekraczał maxSide (PNG); mniejszy zostaje bez zmian (oryginalne bajty).
    // Dekodowanie przy okazji sprawdza, że to obraz: inne dane kończą się wyjątkiem.
    public static (byte[] Bytes, string Mime) Downscale(byte[] source, int maxSide)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(source), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
        if (maxSide <= 0 || longest <= maxSide) return (source, MimeOf(source));

        var scale = maxSide / (double)longest;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new TransformedBitmap(frame, new ScaleTransform(scale, scale))));
        using var output = new MemoryStream();
        encoder.Save(output);
        return (output.ToArray(), "image/png");
    }

    internal static string MimeOf(byte[] bytes) => bytes switch
    {
        [0xFF, 0xD8, ..] => "image/jpeg",
        [0x47, 0x49, 0x46, ..] => "image/gif",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        _ => "image/png",
    };

    public static byte[] FitTo(byte[] source, int width, int height)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(source), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];

        var targetRatio = width / (double)height;
        var cropWidth = frame.PixelWidth;
        var cropHeight = frame.PixelHeight;
        if (frame.PixelWidth / (double)frame.PixelHeight > targetRatio)
            cropWidth = (int)Math.Round(frame.PixelHeight * targetRatio);
        else
            cropHeight = (int)Math.Round(frame.PixelWidth / targetRatio);

        var rect = new Int32Rect((frame.PixelWidth - cropWidth) / 2, (frame.PixelHeight - cropHeight) / 2, cropWidth, cropHeight);
        var cropped = new CroppedBitmap(frame, rect);
        var scaled = new TransformedBitmap(cropped,
            new ScaleTransform(width / (double)cropWidth, height / (double)cropHeight));

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(scaled));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
