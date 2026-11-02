using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using ImageStudio.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ImageStudio.Tests;

// Galeria: kliknięcie tylko pokazuje wynik, prawy przycisk usuwa; wideo da się odtworzyć ponownie
public sealed class GalleryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "is-gallery-" + Guid.NewGuid().ToString("N"));

    public GalleryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private GalleryEntry Image(string name = "a.png", string prompt = "kot na dachu")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, TestImages.Png(8, 8));
        return new GalleryEntry { Prompt = prompt, Kind = "generate", FilePath = path, CostUsd = 0.004m };
    }

    private GalleryEntry Video(string name = "v.mp4")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [0, 0, 0, 0]);
        return new GalleryEntry { Prompt = "fala", Kind = "video", FilePath = path, CostUsd = 0.1m };
    }

    private static TestApp AppWith(params GalleryEntry[] entries)
    {
        var store = new MemoryGalleryStore();
        store.Entries.AddRange(entries);
        return TestApp.Create(s => s.AddSingleton<IGalleryStore>(store));
    }

    // ---- kliknięcie ----

    [Fact]
    public void Selecting_an_image_shows_it_without_putting_its_description_in_the_footer()
    {
        Sta.Run(() =>
        {
            using var app = AppWith(Image());
            var vm = app.Vm;
            var before = vm.Status;

            vm.SelectedGalleryEntry = vm.Gallery[0];

            Assert.NotNull(vm.Image);
            Assert.False(vm.ShowVideo);
            Assert.Equal(before, vm.Status);  // opis jest w dymku miniatury, nie w stopce
        });
    }

    [Fact]
    public void Selecting_a_video_shows_it_without_changing_the_status()
    {
        Sta.Run(() =>
        {
            var entry = Video();
            using var app = AppWith(entry);
            var vm = app.Vm;
            var before = vm.Status;

            vm.SelectedGalleryEntry = vm.Gallery[0];

            Assert.True(vm.ShowVideo);
            Assert.Equal(entry.FilePath, vm.VideoSource!.LocalPath);
            Assert.Equal(before, vm.Status);
        });
    }

    // ---- usuwanie ----

    [Fact]
    public void Remove_takes_the_entry_out_of_the_gallery_and_the_store_but_keeps_the_file()
    {
        Sta.Run(() =>
        {
            var keep = Image("keep.png");
            var remove = Image("remove.png");
            var store = new MemoryGalleryStore();
            store.Entries.AddRange([keep, remove]);
            using var app = TestApp.Create(s => s.AddSingleton<IGalleryStore>(store));
            var vm = app.Vm;
            vm.SelectedGalleryEntry = vm.Gallery.Single(e => e == remove || e.FilePath == remove.FilePath);

            vm.RemoveGalleryEntryCommand.Execute(vm.Gallery.Single(e => e.FilePath == remove.FilePath));

            Assert.Equal([keep.FilePath], vm.Gallery.Select(e => e.FilePath));
            Assert.Equal([keep.FilePath], store.Entries.Select(e => e.FilePath));
            Assert.True(File.Exists(remove.FilePath));
            Assert.Null(vm.SelectedGalleryEntry);
            Assert.Contains("~$0", vm.GalleryTotalLabel.Replace(",", "."));
        });
    }

    [Fact]
    public void Delete_with_file_removes_the_file_after_confirmation_and_keeps_everything_when_declined()
    {
        Sta.Run(() =>
        {
            var entry = Image();
            using var app = AppWith(entry);
            var vm = app.Vm;
            var shown = vm.Gallery[0];

            app.Dialogs.ConfirmAnswer = false;
            vm.DeleteGalleryEntryFileCommand.Execute(shown);
            Assert.True(File.Exists(entry.FilePath));
            Assert.Single(vm.Gallery);

            app.Dialogs.ConfirmAnswer = true;
            vm.DeleteGalleryEntryFileCommand.Execute(shown);
            Assert.False(File.Exists(entry.FilePath));
            Assert.Empty(vm.Gallery);
            Assert.Empty(app.Get<IGalleryStore>().Load());
        });
    }

    [Fact]
    public void Deleting_the_file_of_the_video_being_shown_releases_the_player_first()
    {
        Sta.Run(() =>
        {
            using var app = AppWith(Video());
            var vm = app.Vm;
            vm.SelectedGalleryEntry = vm.Gallery[0];
            Assert.True(vm.ShowVideo);

            vm.DeleteGalleryEntryFileCommand.Execute(vm.Gallery[0]);

            Assert.False(vm.ShowVideo);
            Assert.Null(vm.VideoSource);
            Assert.Empty(vm.Gallery);
        });
    }

    [Fact]
    public void Locked_file_keeps_the_entry_and_reports_the_problem()
    {
        Sta.Run(() =>
        {
            var entry = Image();
            using var app = AppWith(entry);
            var vm = app.Vm;
            using var locked = new FileStream(entry.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);

            vm.DeleteGalleryEntryFileCommand.Execute(vm.Gallery[0]);

            Assert.Single(vm.Gallery);
            Assert.Contains("Nie udało się usunąć pliku", vm.Status);
        });
    }

    [Fact]
    public void Copy_prompt_puts_the_prompt_on_the_clipboard()
    {
        Sta.Run(() =>
        {
            using var app = AppWith(Image(prompt: "lis w śniegu"));

            app.Vm.CopyGalleryPromptCommand.Execute(app.Vm.Gallery[0]);

            Assert.Equal("lis w śniegu", app.Dialogs.CopiedText);
        });
    }

    // ---- widok ----

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    private static void Flush(Dispatcher d) => d.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static (Window Window, OutputPanel Panel) Show(MainViewModel vm)
    {
        var panel = new OutputPanel { DataContext = vm };
        var window = new Window { Width = 900, Height = 700, Content = panel, ShowInTaskbar = false };
        window.Show();
        Flush(window.Dispatcher);
        return (window, panel);
    }

    [Fact]
    public void Gallery_tile_has_a_context_menu_and_a_tooltip_but_no_visible_description()
    {
        WpfHost.Run(() =>
        {
            using var app = AppWith(Image(prompt: "unikalny opis kafelka"));
            var (window, panel) = Show(app.Vm);
            FindAll<Expander>(panel).Last().IsExpanded = true;
            Flush(window.Dispatcher);

            var tile = FindAll<StackPanel>(panel).Single(p => p.ContextMenu is not null);
            Assert.Equal(["Powtórz", null, "Usuń z galerii", "Usuń z galerii i z dysku...", null, "Kopiuj prompt"],
                         tile.ContextMenu!.Items.Cast<object>().Select(i => (i as MenuItem)?.Header as string));
            var tip = (StackPanel)tile.ToolTip;
            Assert.Contains("Prompt", tip.Children.OfType<TextBlock>().Select(t => System.Windows.Data.BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path).ToList());
            var visibleTexts = FindAll<TextBlock>(tile).Where(t => t.IsVisible).Select(t => t.Text).ToList();
            Assert.DoesNotContain("unikalny opis kafelka", visibleTexts);
            Assert.NotEmpty(visibleTexts);  // koszt i rodzaj zostają
            window.Close();
        });
    }

    [Fact]
    public void Video_controls_appear_only_for_video_and_let_the_user_replay_it()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var vm = app.Vm;
            var (window, panel) = Show(vm);
            var controls = (DockPanel)panel.FindName("VideoControls");
            var player = (MediaElement)panel.FindName("Player");
            var playPause = (Button)panel.FindName("PlayPauseButton");
            var loop = (CheckBox)panel.FindName("LoopBox");
            var seek = (Slider)panel.FindName("Seek");
            Button Restart() => FindAll<Button>(controls).Single(b => (b.Content as string)!.Contains("Od początku"));
            Assert.False(controls.IsVisible);

            vm.ShowVideo = true;
            Flush(window.Dispatcher);
            Assert.True(controls.IsVisible);

            // koniec filmu: wideo zostaje, a przycisk proponuje odtworzenie od nowa
            player.RaiseEvent(new RoutedEventArgs(MediaElement.MediaEndedEvent));
            Assert.Equal("▶ Odtwórz ponownie", playPause.Content);
            Assert.Equal(seek.Maximum, seek.Value);

            playPause.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("⏸ Pauza", playPause.Content);
            Assert.Equal(0, seek.Value);

            playPause.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));  // pauza
            Assert.Equal("▶ Odtwórz", playPause.Content);

            player.RaiseEvent(new RoutedEventArgs(MediaElement.MediaEndedEvent));
            Restart().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));  // "od początku" po końcu
            Assert.Equal("⏸ Pauza", playPause.Content);

            // zapętlenie: koniec filmu zaczyna go od nowa
            loop.IsChecked = true;
            player.RaiseEvent(new RoutedEventArgs(MediaElement.MediaEndedEvent));
            Assert.Equal("⏸ Pauza", playPause.Content);

            vm.ShowVideo = false;  // zmiana wyniku na obraz zatrzymuje odtwarzanie
            Flush(window.Dispatcher);
            Assert.Equal("▶ Odtwórz", playPause.Content);
            window.Close();
        });
    }
}
