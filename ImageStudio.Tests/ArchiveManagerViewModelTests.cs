using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ImageStudio.Configuration;
using ImageStudio.Localization;
using ImageStudio.Services;
using ImageStudio.Storage;
using ImageStudio.Storage.Local;
using ImageStudio.ViewModels;
using ImageStudio.Views;

namespace ImageStudio.Tests;

// Panel archiwum na atrapach magazynów (MemoryStoragePlugin): filtr źródła, zaznaczanie, przenoszenie do chmury
public sealed class ArchiveManagerViewModelTests
{
    private static GalleryEntry Entry(string kind, string? storageId, string filePath) =>
        new() { Kind = kind, StorageId = storageId, FilePath = filePath };

    private static (MemoryStoragePlugin local, MemoryStoragePlugin cloud, MediaStorageHost host) Setup(bool cloudActive = true)
    {
        var local = new MemoryStoragePlugin("local");
        var cloud = new MemoryStoragePlugin("cloud");
        var host = new MediaStorageHost([local, cloud]);
        if (cloudActive) host.Apply("cloud", null);
        return (local, cloud, host);
    }

    [Fact]
    public void Lists_only_media_entries_of_the_chosen_source()
    {
        var (local, _, host) = Setup();
        local.Files["local/a.png"] = [1];
        local.Files["local/c.mp4"] = [2];
        var gallery = new ObservableCollection<GalleryEntry>(
        [
            Entry("generate", null, "local/a.png"),
            Entry("edit", "cloud", "cloud/b.png"),
            Entry("video", "local", "local/c.mp4"),
            Entry("transcript", null, "local/t.txt"),
            Entry("generate", null, "local/gone.png"), // brak pliku: nie pokazujemy w źródle lokalnym
        ]);
        var vm = new ArchiveManagerViewModel(gallery, host, () => { }, new FakeDialogs());

        Assert.Equal(["local/a.png", "local/c.mp4"], vm.Items.Select(i => i.Entry.FilePath));

        vm.Source = ArchiveSource.Cloud;
        Assert.Equal(["cloud/b.png"], vm.Items.Select(i => i.Entry.FilePath));
    }

    [Fact]
    public void Selection_count_and_move_command_follow_the_checkboxes()
    {
        var (local, _, host) = Setup();
        local.Files["local/a.png"] = [1];
        local.Files["local/b.png"] = [2];
        var gallery = new ObservableCollection<GalleryEntry>([Entry("generate", null, "local/a.png"), Entry("edit", null, "local/b.png")]);
        var vm = new ArchiveManagerViewModel(gallery, host, () => { }, new FakeDialogs());
        Assert.False(vm.MoveToCloudCommand.CanExecute(null));

        vm.SelectAllCommand.Execute(null);
        Assert.Equal(2, vm.SelectedCount);
        Assert.True(vm.MoveToCloudCommand.CanExecute(null));

        vm.ClearSelectionCommand.Execute(null);
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.MoveToCloudCommand.CanExecute(null));
    }

    [Fact]
    public async Task Moving_selected_entries_updates_gallery_and_saves_after_each()
    {
        var (local, cloud, host) = Setup();
        local.Files["local/a.png"] = [1];
        local.Files["local/b.png"] = [2];
        var a = Entry("generate", null, "local/a.png");
        var b = Entry("edit", null, "local/b.png");
        var gallery = new ObservableCollection<GalleryEntry>([a, b]);
        var saves = 0;
        var vm = new ArchiveManagerViewModel(gallery, host, () => saves++, new FakeDialogs());
        vm.SelectAllCommand.Execute(null);

        await vm.MoveToCloudCommand.ExecuteAsync(null);

        Assert.Equal("cloud", a.StorageId);
        Assert.Equal("cloud/a.png", a.FilePath);
        Assert.Equal("cloud", b.StorageId);
        Assert.Equal(2, saves);
        Assert.Empty(vm.Items);
        Assert.False(local.Files.ContainsKey("local/a.png"));
        Assert.True(cloud.Files.ContainsKey("cloud/b.png"));
        Assert.Equal(Loc.T("archive.status.moved", 2, 0), vm.Status);
        Assert.False(vm.IsMoving);
    }

    [Fact]
    public async Task A_failed_entry_stays_where_it_was_and_the_rest_still_moves()
    {
        var (local, _, host) = Setup();
        local.Files["local/a.png"] = [1];
        local.Files["local/missing.png"] = [3];
        var missing = Entry("generate", null, "local/missing.png");
        var a = Entry("generate", null, "local/a.png");
        var gallery = new ObservableCollection<GalleryEntry>([missing, a]);
        var vm = new ArchiveManagerViewModel(gallery, host, () => { }, new FakeDialogs());
        vm.SelectAllCommand.Execute(null);
        local.Files.Remove("local/missing.png"); // plik znika po wyświetleniu listy: odczyt się nie uda

        await vm.MoveToCloudCommand.ExecuteAsync(null);

        Assert.Equal("cloud", a.StorageId);
        Assert.Null(missing.StorageId);
        Assert.Equal("local/missing.png", missing.FilePath);
        Assert.Single(vm.Items);
        Assert.Same(missing, vm.Items[0].Entry);
        Assert.Equal(Loc.T("archive.status.moved", 1, 1), vm.Status);
    }

    [Fact]
    public void Without_an_active_cloud_the_move_is_blocked_and_the_status_says_so()
    {
        var (local, _, host) = Setup(cloudActive: false);
        local.Files["local/a.png"] = [1];
        var gallery = new ObservableCollection<GalleryEntry>([Entry("generate", null, "local/a.png")]);
        var vm = new ArchiveManagerViewModel(gallery, host, () => { }, new FakeDialogs());
        vm.SelectAllCommand.Execute(null);

        Assert.False(vm.MoveToCloudCommand.CanExecute(null));
        Assert.Equal(Loc.T("archive.status.noCloud"), vm.Status);
    }

    [Fact]
    public void Open_archive_manager_command_shows_the_window_model_on_the_gallery()
    {
        using var app = TestApp.Create();
        var img = app.Get<ImageStudio.Storage.Local.LocalMediaStorage>().Roots[ImageStudio.Storage.MediaKind.Image];
        Directory.CreateDirectory(img);
        var png = Path.Combine(img, "a.png");
        File.WriteAllBytes(png, [1]);
        app.Vm.Gallery.Add(Entry("generate", null, png));
        app.Vm.Gallery.Add(Entry("transcript", null, Path.Combine(img, "t.txt")));

        app.Vm.OpenArchiveManagerCommand.Execute(null);

        Assert.Equal(1, app.Dialogs.ArchiveManagerCount);
        Assert.Single(app.Dialogs.LastArchiveManager!.Items);
    }

    // Folder obrazów i wideo na prawdziwym dysku (katalog tymczasowy): sieroty widoczne, wpisy galerii bez duplikatów
    [Fact]
    public void Local_folder_files_missing_from_the_gallery_are_listed_once()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.png"), [1]);
            File.WriteAllBytes(Path.Combine(dir, "Orphan.PNG"), [2]);
            File.WriteAllBytes(Path.Combine(dir, "clip.mp4"), [3]);
            File.WriteAllBytes(Path.Combine(dir, "notes.txt"), [4]);
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllBytes(Path.Combine(dir, "sub", "deep.png"), [5]);
            var (_, cloud, host) = SetupRealLocal(dir);
            var gallery = new ObservableCollection<GalleryEntry>([Entry("generate", null, Path.Combine(dir, "a.png"))]);

            var vm = new ArchiveManagerViewModel(gallery, host, () => { }, new FakeDialogs());

            Assert.Equal(
                new[] { "a.png", "Orphan.PNG", "clip.mp4" }.Select(n => Path.Combine(dir, n)).Order(),
                vm.Items.Select(i => i.Entry.FilePath).Order());
            Assert.Equal("video", vm.Items.Single(i => i.Entry.FilePath.EndsWith("clip.mp4")).Entry.Kind);
            Assert.Equal("local", vm.Items.Single(i => i.Entry.FilePath.EndsWith("Orphan.PNG")).Entry.StorageId);
            Assert.Empty(cloud.Files);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Moving_an_orphan_adds_it_to_the_gallery()
    {
        var dir = NewTempDir();
        try
        {
            var orphan = Path.Combine(dir, "o.png");
            File.WriteAllBytes(orphan, [7, 7]);
            var (_, cloud, host) = SetupRealLocal(dir);
            var gallery = new ObservableCollection<GalleryEntry>();
            var saves = 0;
            var vm = new ArchiveManagerViewModel(gallery, host, () => saves++, new FakeDialogs());
            vm.SelectAllCommand.Execute(null);

            await vm.MoveToCloudCommand.ExecuteAsync(null);

            var moved = Assert.Single(gallery);
            Assert.Equal("cloud", moved.StorageId);
            Assert.Equal("cloud/o.png", moved.FilePath);
            Assert.Equal([7, 7], cloud.Files["cloud/o.png"]);
            Assert.False(File.Exists(orphan));
            Assert.Equal(1, saves);
            Assert.Empty(vm.Items);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Jednopikselowy PNG (1x1) do podglądu
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    [Fact]
    public async Task Choosing_a_thumbnail_loads_its_full_image_into_the_preview()
    {
        var (local, _, host) = Setup();
        local.Files["local/a.png"] = OnePixelPng;
        var vm = new ArchiveManagerViewModel(new ObservableCollection<GalleryEntry>([Entry("generate", null, "local/a.png")]), host, () => { }, new FakeDialogs());

        await vm.PreviewItemCommand.ExecuteAsync(vm.Items[0]);

        Assert.Same(vm.Items[0], vm.Preview);
        Assert.Equal("a.png", vm.PreviewFileName);
        Assert.NotNull(vm.PreviewImage);
        Assert.Equal(1, vm.PreviewImage!.PixelWidth);
        Assert.True(vm.CopyPreviewCommand.CanExecute(null));
    }

    [Fact]
    public async Task Copy_hands_the_preview_image_to_the_dialog_service_and_reports_it()
    {
        var (local, _, host) = Setup();
        local.Files["local/a.png"] = OnePixelPng;
        var dialogs = new FakeDialogs();
        var vm = new ArchiveManagerViewModel(new ObservableCollection<GalleryEntry>([Entry("generate", null, "local/a.png")]), host, () => { }, dialogs);
        await vm.PreviewItemCommand.ExecuteAsync(vm.Items[0]);

        vm.CopyPreviewCommand.Execute(null);

        Assert.Same(vm.PreviewImage, Assert.Single(dialogs.Copied));
        Assert.Equal(Loc.T("archive.status.copied"), vm.Status);
    }

    [Fact]
    public async Task Video_preview_shows_its_thumbnail_but_cannot_be_copied()
    {
        var (local, _, host) = Setup();
        local.Files["local/c.mp4"] = [2];
        var video = Entry("video", null, "local/c.mp4");
        video.Thumbnail = new System.Windows.Media.Imaging.WriteableBitmap(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        var dialogs = new FakeDialogs();
        var vm = new ArchiveManagerViewModel(new ObservableCollection<GalleryEntry>([video]), host, () => { }, dialogs);

        await vm.PreviewItemCommand.ExecuteAsync(vm.Items[0]);

        Assert.NotNull(vm.PreviewImage);
        Assert.False(vm.CopyPreviewCommand.CanExecute(null));
        Assert.Empty(dialogs.Copied);
    }

    [Fact]
    public async Task A_read_error_leaves_the_preview_empty_and_does_not_throw()
    {
        var (local, _, host) = Setup();
        local.Files["local/a.png"] = OnePixelPng;
        var vm = new ArchiveManagerViewModel(new ObservableCollection<GalleryEntry>([Entry("generate", null, "local/a.png")]), host, () => { }, new FakeDialogs());
        local.Files.Remove("local/a.png"); // plik znika po wyświetleniu listy: odczyt się nie uda

        await vm.PreviewItemCommand.ExecuteAsync(vm.Items[0]);

        Assert.Same(vm.Items[0], vm.Preview);
        Assert.Null(vm.PreviewImage);
        Assert.False(vm.CopyPreviewCommand.CanExecute(null));
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "imagestudio-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Dysk lokalny na katalogu tymczasowym (obrazy i wideo), chmura na atrapie
    private static (LocalMediaStorage local, MemoryStoragePlugin cloud, MediaStorageHost host) SetupRealLocal(string dir)
    {
        var local = new LocalMediaStorage { Roots = { [MediaKind.Image] = dir, [MediaKind.Video] = dir, [MediaKind.Audio] = dir } };
        var cloud = new MemoryStoragePlugin("cloud");
        var host = new MediaStorageHost([local, cloud]);
        host.Apply("cloud", null);
        return (local, cloud, host);
    }
}

public sealed class ArchiveManagerWindowTests
{
    [Fact]
    public void Window_opens_with_the_view_model_and_uses_the_window_style()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var vm = new ImageStudio.ViewModels.ArchiveManagerViewModel(
                new System.Collections.ObjectModel.ObservableCollection<ImageStudio.Configuration.GalleryEntry>(),
                app.Get<ImageStudio.Services.MediaStorageHost>(), () => { }, new FakeDialogs());
            var window = new ImageStudio.Views.ArchiveManagerWindow { DataContext = vm, ShowInTaskbar = false };
            window.Show();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            Assert.Same(System.Windows.Application.Current.FindResource("WindowBase"), window.Style);
            Assert.Equal(Loc.T("archive.title"), window.Title);
            window.Close();
        });
    }

    // Motyw ciemny: napisy okna (radio, etykiety wyboru, tekst miniatur i podglądu) muszą być czytelne na tle, a nie czarne
    [Fact]
    public void Texts_in_the_window_are_readable_in_the_dark_theme()
    {
        WpfHost.Run(() =>
        {
            ThemeService.Apply("dark");
            var dir = Path.Combine(Path.GetTempPath(), "imagestudio-archive-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllBytes(Path.Combine(dir, "a.png"), [1]);
                var local = new LocalMediaStorage { Roots = { [MediaKind.Image] = dir, [MediaKind.Video] = dir, [MediaKind.Audio] = dir } };
                var host = new MediaStorageHost([local, new MemoryStoragePlugin("cloud")]);
                var vm = new ImageStudio.ViewModels.ArchiveManagerViewModel(
                    new System.Collections.ObjectModel.ObservableCollection<GalleryEntry>(), host, () => { }, new FakeDialogs());
                var window = new ImageStudio.Views.ArchiveManagerWindow { DataContext = vm, ShowInTaskbar = false };
                window.Show();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                var backgrounds = new[] { BrushColor("WindowBgBrush"), BrushColor("SurfaceAltBrush") };
                var foregrounds = FindAll<RadioButton>(window).Select(r => r.Foreground)
                    .Concat(FindAll<CheckBox>(window).Select(c => c.Foreground))
                    .Concat(FindAll<TextBlock>(window).Select(t => t.Foreground))
                    .ToList();
                Assert.True(foregrounds.Count >= 6, $"za mało kontrolek z tekstem: {foregrounds.Count}");
                foreach (var brush in foregrounds)
                {
                    var color = Assert.IsType<SolidColorBrush>(brush).Color;
                    foreach (var background in backgrounds)
                    {
                        Assert.True(Contrast(color, background) >= 3.0, $"napis {color} na tle {background}: {Contrast(color, background):0.00}");
                    }
                }
                window.Close();
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        });
    }

    private static Color BrushColor(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color c)
        {
            static double Lin(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }
}
