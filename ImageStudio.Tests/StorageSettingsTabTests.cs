using ImageStudio.Services;
using ImageStudio.Storage;
using ImageStudio.Storage.Local;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

public sealed class StorageSettingsTabTests
{
    [Fact]
    public async Task Changed_folder_is_applied_to_new_files_and_remembered_in_the_user_state()
    {
        using var app = TestApp.Create();
        var folder = Path.Combine(Path.GetTempPath(), "is-store-" + Guid.NewGuid().ToString("N"));
        app.Get<FakeDialogs>().SettingsAnswer = r =>
        {
            var tab = new StorageSettingsTab(r, app.Get<MediaStorageHost>());
            tab.Selected!.Fields.Single(f => f.Key == "image").Value = folder;
            return tab.Apply(r);
        };

        await app.Vm.OpenSettingsCommand.ExecuteAsync(null);

        try
        {
            Assert.Equal(folder, app.Get<LocalMediaStorage>().Roots[MediaKind.Image]);
            Assert.Equal(folder, app.State.State.StorageConfig["local"]["image"]);
            Assert.Equal("local", app.State.State.StorageId);

            var path = await app.Get<MediaStorageHost>().WriteAsync(MediaKind.Image, "a.png", new byte[] { 1 });
            Assert.Equal(Path.Combine(folder, "a.png"), path);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Empty_folder_restores_the_default_and_unknown_plugin_falls_back_to_the_first()
    {
        var local = new LocalMediaStorage();
        var defaultImages = local.Roots[MediaKind.Image];
        var host = new MediaStorageHost([local]);

        host.Apply("zniknela", new Dictionary<string, Dictionary<string, string>> { ["local"] = new() { ["image"] = "C:\\x" } });
        Assert.Equal("C:\\x", local.Roots[MediaKind.Image]);
        Assert.Same(local, host.Active);

        host.Apply("local", new Dictionary<string, Dictionary<string, string>> { ["local"] = new() { ["image"] = " " } });
        Assert.Equal(defaultImages, local.Roots[MediaKind.Image]);
    }
}
