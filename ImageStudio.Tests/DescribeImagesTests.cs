using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

public sealed class DescribeImagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "is-describe-" + Guid.NewGuid().ToString("N"));

    public DescribeImagesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private string Png(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, TestImages.Png(8, 8));
        return path;
    }

    private static (MainViewModel Vm, FakeDialogs Dialogs, FakeProvider Provider) Create()
    {
        var dialogs = new FakeDialogs();
        var provider = new FakeProvider { OnChat = (_, _) => "  Kot siedzi na parapecie.  " };
        var vm = new MainViewModel(provider, dialogs, new ImageDownloadService(), AppSettings.Load())
        {
            SelectedEnhancerModel = "vision-model",
        };
        return (vm, dialogs, provider);
    }

    [Fact]
    public void Edit_editor_describes_reference_images_with_selected_model()
    {
        Sta.Run(() =>
        {
            var (vm, dialogs, provider) = Create();
            vm.References.Add(new ReferenceItem(Png("a.png"), "a", "a"));
            vm.References.Add(new ReferenceItem(Png("b.png"), "b", "b"));
            vm.EditEditPromptCommand.Execute(null);

            var text = dialogs.LastDescribe!().GetAwaiter().GetResult();

            Assert.Equal("Kot siedzi na parapecie.", text);
            Assert.Equal("vision-model", provider.LastModel);
            var user = provider.LastMessages.Single(m => m.Role == "user");
            Assert.Equal(2, user.Images!.Count);
        });
    }

    [Fact]
    public void Generate_editor_asks_for_files_and_cancel_returns_empty()
    {
        Sta.Run(() =>
        {
            var (vm, dialogs, provider) = Create();
            vm.EditGeneratePromptCommand.Execute(null);

            dialogs.PickResult = null;
            Assert.Equal("", dialogs.LastDescribe!().GetAwaiter().GetResult());
            Assert.Equal(0, provider.ChatCalls);

            dialogs.PickResult = [Png("c.png")];
            Assert.NotEqual("", dialogs.LastDescribe!().GetAwaiter().GetResult());
            Assert.Single(provider.LastMessages.Single(m => m.Role == "user").Images!);
        });
    }

    [Fact]
    public void Describe_without_model_reports_error()
    {
        Sta.Run(() =>
        {
            var (vm, dialogs, _) = Create();
            vm.SelectedEnhancerModel = null;
            vm.EditGeneratePromptCommand.Execute(null);

            Assert.Throws<InvalidOperationException>(() => dialogs.LastDescribe!().GetAwaiter().GetResult());
        });
    }
}
