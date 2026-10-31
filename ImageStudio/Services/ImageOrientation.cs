using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageStudio.Configuration;

namespace ImageStudio.Services;

// Wykrywa orientację z tagu EXIF (274) i zapisuje obraz wyprostowany.
// WPF nie stosuje tego tagu sam, więc bez tego podgląd, proporcje i API dostają zdjęcie obrócone.
public static class ImageOrientation
{
    private const string OrientationQuery = "/app1/ifd/{ushort=274}";

    // Zwraca to samo wejście, gdy orientacja jest normalna (1) lub brak tagu; inaczej PNG w orientacji normalnej
    public static byte[] Normalize(byte[] source)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(source), BitmapCreateOptions.PreservePixelFormat,
                                         BitmapCacheOption.OnLoad).Frames[0];
        var orientation = ReadOrientation(frame);
        if (orientation == 1) return source;

        var upright = new TransformedBitmap(frame, Transform(orientation));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(upright));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    // Ścieżka do pliku w orientacji normalnej; oryginał, jeśli nie trzeba zmieniać
    public static string EnsureUpright(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var upright = Normalize(bytes);
        if (ReferenceEquals(upright, bytes)) return path;

        var dir = Path.Combine(AppPaths.UserDataDir, "refs");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"{Guid.NewGuid():N}.png");
        File.WriteAllBytes(target, upright);
        return target;
    }

    private static int ReadOrientation(BitmapFrame frame)
    {
        // Brak metadanych (PNG, WEBP bez EXIF) oznacza orientację normalną
        try
        {
            return frame.Metadata is System.Windows.Media.Imaging.BitmapMetadata meta
                   && meta.GetQuery(OrientationQuery) is ushort value
                ? value
                : 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    // Transformacje zgodne ze specyfikacją EXIF (1-8) dla współrzędnych ekranowych (oś Y w dół)
    private static Transform Transform(int orientation)
    {
        var group = new TransformGroup();
        switch (orientation)
        {
            case 2: group.Children.Add(new ScaleTransform(-1, 1)); break;
            case 3: group.Children.Add(new RotateTransform(180)); break;
            case 4: group.Children.Add(new ScaleTransform(1, -1)); break;
            case 5: group.Children.Add(new RotateTransform(90)); group.Children.Add(new ScaleTransform(-1, 1)); break;
            case 6: group.Children.Add(new RotateTransform(90)); break;
            case 7: group.Children.Add(new RotateTransform(270)); group.Children.Add(new ScaleTransform(-1, 1)); break;
            case 8: group.Children.Add(new RotateTransform(270)); break;
        }
        return group;
    }
}
