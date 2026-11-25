using System.Collections.ObjectModel;
using ImageStudio.Configuration;
using ImageStudio.Localization;
using ImageStudio.Services;
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
        var (_, _, host) = Setup();
        var gallery = new ObservableCollection<GalleryEntry>(
        [
            Entry("generate", null, "local/a.png"),
            Entry("edit", "cloud", "cloud/b.png"),
            Entry("video", "local", "local/c.mp4"),
            Entry("transcript", null, "local/t.txt"),
        ]);
        var vm = new ArchiveManagerViewModel(gallery, host, () => { });

        Assert.Equal(["local/a.png", "local/c.mp4"], vm.Items.Select(i => i.Entry.FilePath));

        vm.Source = ArchiveSource.Cloud;
        Assert.Equal(["cloud/b.png"], vm.Items.Select(i => i.Entry.FilePath));
    }

    [Fact]
    public void Selection_count_and_move_command_follow_the_checkboxes()
    {
        var (_, _, host) = Setup();
        var gallery = new ObservableCollection<GalleryEntry>([Entry("generate", null, "local/a.png"), Entry("edit", null, "local/b.png")]);
        var vm = new ArchiveManagerViewModel(gallery, host, () => { });
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
        var vm = new ArchiveManagerViewModel(gallery, host, () => saves++);
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
        local.Files["local/a.png"] = [1]; // "missing.png" nie istnieje: odczyt się nie uda
        var missing = Entry("generate", null, "local/missing.png");
        var a = Entry("generate", null, "local/a.png");
        var gallery = new ObservableCollection<GalleryEntry>([missing, a]);
        var vm = new ArchiveManagerViewModel(gallery, host, () => { });
        vm.SelectAllCommand.Execute(null);

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
        var (_, _, host) = Setup(cloudActive: false);
        var gallery = new ObservableCollection<GalleryEntry>([Entry("generate", null, "local/a.png")]);
        var vm = new ArchiveManagerViewModel(gallery, host, () => { });
        vm.SelectAllCommand.Execute(null);

        Assert.False(vm.MoveToCloudCommand.CanExecute(null));
        Assert.Equal(Loc.T("archive.status.noCloud"), vm.Status);
    }

    [Fact]
    public void Open_archive_manager_command_shows_the_window_model_on_the_gallery()
    {
        using var app = TestApp.Create();
        app.Vm.Gallery.Add(Entry("generate", null, "img/a.png"));
        app.Vm.Gallery.Add(Entry("transcript", null, "img/t.txt"));

        app.Vm.OpenArchiveManagerCommand.Execute(null);

        Assert.Equal(1, app.Dialogs.ArchiveManagerCount);
        Assert.Single(app.Dialogs.LastArchiveManager!.Items);
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
                app.Get<ImageStudio.Services.MediaStorageHost>(), () => { });
            var window = new ImageStudio.Views.ArchiveManagerWindow { DataContext = vm, ShowInTaskbar = false };
            window.Show();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            Assert.Same(System.Windows.Application.Current.FindResource("WindowBase"), window.Style);
            Assert.Equal(Loc.T("archive.title"), window.Title);
            window.Close();
        });
    }
}
