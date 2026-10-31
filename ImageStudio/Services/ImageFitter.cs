using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageStudio.Services;

// Dopasowuje wynik do wybranych proporcji W:H: przycięcie środkiem, potem skalowanie do W×H.
// Używane przy edycji, bo endpoint edycji nie respektuje parametru rozmiaru.
public static class ImageFitter
{
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
