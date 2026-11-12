using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.Localization;
using ImageStudio.Services;
using ImageStudio.Tabs.Edit;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

public sealed class ReferenceOrderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "is-reference-tests-" + Guid.NewGuid().ToString("N"));

    public ReferenceOrderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private string Picture(string name, int width, int height, bool jpeg = false)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, jpeg ? TestImages.Jpeg(width, height) : TestImages.Png(width, height));
        return path;
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
    public void Moving_and_removing_references_updates_primary_and_preserves_order()
    {
        Sta.Run(() =>
        {
            var a = Picture("a.png", 40, 20);
            var b = Picture("b.jpg", 20, 40, jpeg: true);
            var c = Picture("c.png", 30, 30);
            var dialogs = new FakeDialogs { PickResult = [a, b, c] };
            var vm = TestApp.CreateVm(new FakeProvider(), dialogs);
            vm.AddReferencesCommand.Execute(null);
            var first = vm.References[0];
            var second = vm.References[1];
            var third = vm.References[2];
            Assert.Equal(40, vm.SourceImage!.PixelWidth);

            vm.MoveReferenceCommand.Execute([2, 1]);
            Assert.Equal([a, c, b], vm.References.Select(r => r.Path));
            Assert.Equal(40, vm.SourceImage!.PixelWidth);
            vm.MoveReferenceLeftCommand.Execute(third);
            Assert.Equal([c, a, b], vm.References.Select(r => r.Path));
            Assert.Equal((30, 30), (vm.SourceImage!.PixelWidth, vm.SourceImage.PixelHeight));
            Assert.Equal((ImageAspect.Snap(30), ImageAspect.Snap(30)), (vm.Width, vm.Height));
            vm.MoveReferenceRightCommand.Execute(third);
            Assert.Equal([a, c, b], vm.References.Select(r => r.Path));
            Assert.Equal((40, 20), (vm.SourceImage!.PixelWidth, vm.SourceImage.PixelHeight));
            Assert.Equal((ImageAspect.Snap(40), ImageAspect.Snap(20)), (vm.Width, vm.Height));

            vm.MoveReferenceCommand.Execute([0, 2]);
            Assert.Equal([c, b, a], vm.References.Select(r => r.Path));
            Assert.Equal((30, 30), (vm.SourceImage!.PixelWidth, vm.SourceImage.PixelHeight));
            vm.MoveReferenceCommand.Execute([2, 0]);
            vm.RemoveReferenceCommand.Execute(first);
            Assert.Equal((30, 30), (vm.SourceImage!.PixelWidth, vm.SourceImage.PixelHeight));
            vm.RemoveReferenceCommand.Execute(third);
            Assert.Same(second, vm.References[0]);
            Assert.Equal((20, 40), (vm.SourceImage!.PixelWidth, vm.SourceImage.PixelHeight));
            vm.ClearReferencesCommand.Execute(null);
            Assert.Null(vm.SourceImage);
        });
    }

    [Fact]
    public void Identical_references_are_moved_and_removed_by_instance()
    {
        Sta.Run(() =>
        {
            var path = Picture("same.png", 40, 20);
            var vm = TestApp.CreateVm(new FakeProvider(), new FakeDialogs { PickResult = [path, path] });
            vm.AddReferencesCommand.Execute(null);
            var first = vm.References[0];
            var second = vm.References[1];
            vm.MoveReferenceLeftCommand.Execute(second);
            Assert.Same(second, vm.References[0]);
            vm.RemoveReferenceCommand.Execute(first);
            Assert.Same(second, Assert.Single(vm.References));
            Assert.Equal(40, vm.SourceImage!.PixelWidth);
        });
    }

    [Fact]
    public void Invalid_moves_and_missing_new_primary_leave_state_unchanged()
    {
        Sta.Run(() =>
        {
            var a = Picture("a.png", 40, 20);
            var b = Picture("b.png", 20, 40);
            var vm = TestApp.CreateVm(new FakeProvider(), new FakeDialogs { PickResult = [a, b] });
            vm.AddReferencesCommand.Execute(null);
            var original = vm.SourceImage;
            var first = vm.References[0];
            Assert.False(vm.MoveReferenceLeftCommand.CanExecute(first));
            Assert.False(vm.MoveReferenceRightCommand.CanExecute(vm.References[1]));
            vm.MoveReferenceLeftCommand.Execute(first);
            vm.MoveReferenceCommand.Execute([-1, 0]);
            File.Delete(b);
            vm.MoveReferenceCommand.Execute([1, 0]);
            Assert.Equal([a, b], vm.References.Select(r => r.Path));
            Assert.Same(original, vm.SourceImage);
        });
    }

    [Fact]
    public async Task Edit_sends_references_in_the_changed_order()
    {
        await WpfHost.RunAsync(async () =>
        {
            var a = Picture("a.png", 40, 20);
            var b = Picture("b.png", 20, 40);
            var provider = new FakeProvider
            {
                ImageCatalog = new ImageModels([], [new ImageModelInfo("edit/test", "test", false, ["image"])]),
                OnEditImage = (_, _, _) => new ImageResult(TestImages.Png(40, 40), null),
            };
            using var app = TestApp.Create(provider: provider, dialogs: new FakeDialogs { PickResult = [a, b] });
            var vm = app.Vm;
            await vm.InitializeAsync();
            vm.EditPrompt = "test";
            vm.AddReferencesCommand.Execute(null);
            vm.MoveReferenceCommand.Execute([1, 0]);

            await vm.EditCommand.ExecuteAsync(null);

            Assert.Equal([b, a], provider.LastEdit!.Value.Paths);
        });
    }

    [Fact]
    public void Tiles_bind_to_all_references_and_buttons_reorder_and_remove()
    {
        WpfHost.Run(() =>
        {
            var a = Picture("a.png", 40, 20);
            var b = Picture("b.jpg", 20, 40, jpeg: true);
            using var app = TestApp.Create(dialogs: new FakeDialogs { PickResult = [a, b] });
            var vm = app.Vm;
            vm.AddReferencesCommand.Execute(null);
            var panel = new EditPanel { DataContext = vm };
            var window = new Window { Content = panel, Width = 600, Height = 800, ShowInTaskbar = false };
            try
            {
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var list = (ListBox)panel.FindName("ReferenceList");
                Assert.Same(vm.References, list.ItemsSource);
                Assert.Equal(2, list.Items.Count);
                Assert.Equal(2, FindAll<Image>(list).Count(image => image.Source is not null));
                var firstTile = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                var firstEarlier = FindAll<Button>(firstTile).Single(button => AutomationProperties.GetName(button) == Loc.T("edit.refs.moveLeft"));
                Assert.False(firstEarlier.IsEnabled);
                Assert.Contains(FindAll<TextBlock>(firstTile), text => text.Text == Loc.T("edit.refs.primary") && text.Visibility == Visibility.Visible);
                var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
                var earlier = FindAll<Button>(container).Single(button => AutomationProperties.GetName(button) == Loc.T("edit.refs.moveLeft"));
                Assert.True(earlier.IsEnabled);
                earlier.Command.Execute(earlier.CommandParameter);
                Assert.Equal([b, a], vm.References.Select(r => r.Path));
                Assert.Equal(20, vm.SourceImage!.PixelWidth);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                var remove = FindAll<Button>(container).Single(button => AutomationProperties.GetName(button) == Loc.T("edit.refs.remove"));
                remove.Command.Execute(remove.CommandParameter);
                Assert.Equal(a, Assert.Single(vm.References).Path);
                Assert.Equal(40, vm.SourceImage!.PixelWidth);
            }
            finally { window.Close(); }
        });
    }
}
