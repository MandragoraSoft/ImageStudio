using System.Windows.Controls;
using ImageStudio.Configuration;
using ImageStudio.Views;

namespace ImageStudio.Tests;

// Testy błędów, które wcześniej uniemożliwiały start aplikacji lub zapis kluczy
public sealed class RegressionTests : IDisposable
{
    // 1x1 PNG
    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "imagestudio-tests-" + Guid.NewGuid().ToString("N"));

    public RegressionTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Gallery_thumbnail_of_a_missing_file_is_null()
    {
        Assert.Null(GalleryStore.Thumbnail(Path.Combine(_dir, "nie-ma.png")));
    }

    [Fact]
    public void Gallery_thumbnail_of_a_video_does_not_throw()
    {
        // wpis wideo w galerii wywalał start aplikacji (NotSupportedException z dekodera obrazów)
        var video = Path.Combine(_dir, "klip.mp4");
        File.WriteAllBytes(video, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x6D, 0x70, 0x34, 0x32]);

        Assert.Null(GalleryStore.Thumbnail(video));
    }

    [Fact]
    public void Gallery_thumbnail_of_an_image_is_frozen_and_small()
    {
        var png = Path.Combine(_dir, "obraz.png");
        File.WriteAllBytes(png, Convert.FromBase64String(PngBase64));

        var thumbnail = GalleryStore.Thumbnail(png);

        Assert.NotNull(thumbnail);
        Assert.True(thumbnail.IsFrozen);
    }

    [Fact]
    public void PasswordBox_with_empty_initial_value_still_pushes_typed_text()
    {
        // pusty klucz API nie podpinał PasswordChanged, więc wpisany token nigdy nie trafiał do ViewModelu
        Sta.Run(() =>
        {
            var box = new PasswordBox();
            PasswordBoxBehavior.SetBoundPassword(box, "");

            box.Password = "sk-or-sekret";

            Assert.Equal("sk-or-sekret", PasswordBoxBehavior.GetBoundPassword(box));
        });
    }

    [Fact]
    public void PasswordBox_shows_value_pushed_from_the_view_model()
    {
        Sta.Run(() =>
        {
            var box = new PasswordBox();

            PasswordBoxBehavior.SetBoundPassword(box, "zapisany-klucz");

            Assert.Equal("zapisany-klucz", box.Password);
        });
    }
}
