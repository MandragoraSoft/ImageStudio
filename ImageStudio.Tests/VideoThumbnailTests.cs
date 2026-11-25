using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.Configuration;
using ImageStudio.Views;

namespace ImageStudio.Tests;

// Miniatura wideo w galerii: klatka z powłoki Windows z pamięcią podręczną na dysku, ikona zastępcza bez niej
public sealed class VideoThumbnailTests
{
    [Fact]
    public void Real_video_gets_a_shell_thumbnail_that_is_cached_on_disk()
    {
        // Używa prawdziwego wideo z folderu zapisów, jeśli aplikacja już jakieś wygenerowała; bez próbki nic nie sprawdza
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ImageStudio");
        var file = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.mp4").FirstOrDefault() : null;
        if (file is null) return;

        Sta.Run(() =>
        {
            var first = GalleryStore.Thumbnail(file);
            Assert.NotNull(first);
            Assert.True(first.IsFrozen);
            Assert.InRange(first.PixelWidth, 1, 160);

            Assert.NotEmpty(Directory.GetFiles(Path.Combine(AppPaths.UserDataDir, "thumbs"), "*.png"));
            var cached = GalleryStore.Thumbnail(file);  // drugi raz z pliku pamięci podręcznej
            Assert.Equal((first.PixelWidth, first.PixelHeight), (cached!.PixelWidth, cached.PixelHeight));
        });
    }

    [Fact]
    public void Broken_or_empty_video_files_give_no_thumbnail_instead_of_failing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "is-thumb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var junk = Path.Combine(dir, "zly.mp4");
            File.WriteAllBytes(junk, [1, 2, 3, 4, 5]);
            var empty = Path.Combine(dir, "pusty.mp4");
            File.WriteAllBytes(empty, []);

            Sta.Run(() =>
            {
                Assert.Null(GalleryStore.Thumbnail(junk));
                Assert.Null(GalleryStore.Thumbnail(empty));
                Assert.Null(GalleryStore.Thumbnail(Path.Combine(dir, "nie-ma.mp4")));
            });
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("a.mp4", true)]
    [InlineData("B.MOV", true)]
    [InlineData("c.webm", true)]
    [InlineData("d.png", false)]
    [InlineData("e", false)]
    public void Video_files_are_recognised_by_extension(string name, bool expected)
    {
        Assert.Equal(expected, VideoThumbnail.IsVideoFile(name));
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    [Fact]
    public void Tile_without_a_thumbnail_shows_the_video_icon_and_every_video_gets_a_play_badge()
    {
        WpfHost.Run(() =>
        {
            var store = new MemoryGalleryStore();
            store.Entries.Add(new GalleryEntry { Prompt = "wideo bez klatki", Kind = "video", FilePath = "nie-ma.mp4" });
            store.Entries.Add(new GalleryEntry { Prompt = "obraz", Kind = "generate", FilePath = "nie-ma.png" });
            using var app = TestApp.Create(s => Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IGalleryStore>(s, store));
            var panel = new OutputPanel { DataContext = app.Vm };
            var window = new Window { Width = 900, Height = 700, Content = panel, ShowInTaskbar = false };
            window.Show();
            FindAll<Expander>(panel).Last().IsExpanded = true;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var tiles = FindAll<StackPanel>(panel).Where(p => p.ContextMenu is not null).ToList();
            Assert.Equal(2, tiles.Count);
            // Znaczek magazynu (dysk E8B7 / chmura E753, lewy górny róg) jest na każdym kafelku, pomijamy go tutaj
            const string LocalGlyph = "", CloudGlyph = "";
            IEnumerable<string> VisibleGlyphs(StackPanel tile) =>
                FindAll<TextBlock>(tile).Where(t => t.IsVisible && t.FontFamily.Source == "Segoe MDL2 Assets" && t.Text is not (LocalGlyph or CloudGlyph)).Select(t => t.Text);

            var video = tiles.Single(t => ((GalleryEntry)t.DataContext).IsVideo);
            var image = tiles.Single(t => !((GalleryEntry)t.DataContext).IsVideo);
            Assert.Equal(["", ""], VisibleGlyphs(video));  // ikona zastępcza i znaczek odtwarzania
            Assert.Empty(VisibleGlyphs(image));
            Assert.All(tiles, t => Assert.Single(FindAll<TextBlock>(t).Where(x => x.IsVisible && x.Text == LocalGlyph)));
            window.Close();
        });
    }
}
