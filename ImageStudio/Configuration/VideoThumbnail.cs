using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ImageStudio.Configuration;

// Miniatura wideo: klatka z pliku pobrana z powłoki Windows (ta sama, którą pokazuje Eksplorator; WIC nie czyta mp4).
// Wynik trafia do pamięci podręcznej na dysku (thumbs\), więc galeria z wieloma wideo nie liczy klatek przy każdym starcie.
// Brak miniatury (uszkodzony plik, brak kodeka) to null: widok pokazuje wtedy ikonę wideo
public static class VideoThumbnail
{
    public static readonly string[] Extensions = [".mp4", ".mov", ".webm", ".mkv", ".avi"];

    public static bool IsVideoFile(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static BitmapSource? TryGet(string path, int size = 160)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0) return null;

            var cache = CachePath(info, size);
            if (File.Exists(cache) && LoadPng(cache) is { } cached) return cached;

            var frame = FromShell(path, size);
            if (frame is null) return null;

            Save(frame, cache);
            return frame;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException
                                      or InvalidOperationException or NotSupportedException or InvalidCastException)
        {
            return null;
        }
    }

    // Klucz pamięci podręcznej zmienia się razem z plikiem (długość, czas zapisu) i rozmiarem miniatury
    private static string CachePath(FileInfo file, int size)
    {
        var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{size}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(AppPaths.UserDataDir, "thumbs", hash + ".png");
    }

    private static BitmapSource? LoadPng(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException)
        {
            return null;  // uszkodzony plik pamięci podręcznej: miniatura powstanie od nowa
        }
    }

    private static void Save(BitmapSource frame, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Pamięć podręczna jest wygodą: miniatura i tak jest zwrócona
        }
    }

    private static BitmapSource? FromShell(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
        try
        {
            // THUMBNAILONLY: bez ikony zastępczej, gdy powłoka nie umie wydobyć klatki
            var hr = factory.GetImage(new NativeSize { Width = size, Height = size }, ThumbnailOnly, out var bitmap);
            if (hr != 0 || bitmap == IntPtr.Zero) return null;

            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    private const int ThumbnailOnly = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
                                                           [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
