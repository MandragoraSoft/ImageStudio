using System.Net;
using System.Text.Json;
using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

// Obrazy w czacie: kodowanie, zmniejszanie i załączniki
public class ChatImageTests
{
    [Fact]
    public void Data_uri_carries_mime_and_base64()
    {
        var image = new ChatImage([1, 2, 3], "image/jpeg");

        Assert.Equal("data:image/jpeg;base64,AQID", image.DataUri);
    }

    [Fact]
    public void Preview_decodes_a_valid_image_and_is_null_for_garbage()
    {
        var valid = new ChatImage(TestImages.Png(10, 10), "image/png");
        var garbage = new ChatImage([1, 2, 3, 4], "image/png");

        Assert.NotNull(valid.Preview);
        Assert.True(valid.Preview.IsFrozen);
        Assert.Same(valid.Preview, valid.Preview);
        Assert.Null(garbage.Preview);
    }

    [Fact]
    public void Downscale_shrinks_large_images_keeping_the_ratio()
    {
        var (bytes, mime) = ImageFitter.Downscale(TestImages.Png(400, 200), 100);

        Assert.Equal("image/png", mime);
        Assert.Equal((100, 50), TestImages.Size(bytes));
    }

    [Fact]
    public void Downscale_leaves_small_images_untouched()
    {
        var jpeg = TestImages.Jpeg(60, 40);

        var (bytes, mime) = ImageFitter.Downscale(jpeg, 100);

        Assert.Same(jpeg, bytes);
        Assert.Equal("image/jpeg", mime);
        Assert.Same(jpeg, ImageFitter.Downscale(jpeg, 0).Bytes);
    }

    [Fact]
    public void Downscale_rejects_data_that_is_not_an_image()
    {
        Assert.ThrowsAny<Exception>(() => ImageFitter.Downscale([1, 2, 3, 4, 5], 100));
    }

    [Fact]
    public void Mime_is_detected_from_the_file_signature()
    {
        Assert.Equal("image/jpeg", ImageFitter.MimeOf([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("image/gif", ImageFitter.MimeOf([0x47, 0x49, 0x46, 0x38, 0x39, 0x61]));
        Assert.Equal("image/webp", ImageFitter.MimeOf([0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50]));
        Assert.Equal("image/png", ImageFitter.MimeOf([0x89, 0x50, 0x4E, 0x47]));
    }

    // --- format żądania ---

    private static async Task<JsonDocument> SendAsync(params ChatMessage[] history)
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"}}]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        await OpenAiCompat.ChatAsync(http, "/v1/chat/completions", "m", history, null, default);
        return JsonDocument.Parse(handler.RequestBody!);
    }

    [Fact]
    public async Task User_message_with_images_is_sent_as_text_and_image_url_parts()
    {
        var image = new ChatImage([1, 2, 3], "image/png");

        using var body = await SendAsync(new ChatMessage("user", "co to jest?", Images: [image]));

        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("co to jest?", content[0].GetProperty("text").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,AQID", content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task Image_only_message_has_no_text_part()
    {
        using var body = await SendAsync(new ChatMessage("user", "", Images: [new ChatImage([1], "image/png"), new ChatImage([2], "image/jpeg")]));

        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.All(content.EnumerateArray(), part => Assert.Equal("image_url", part.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task Message_without_images_keeps_plain_string_content()
    {
        using var body = await SendAsync(new ChatMessage("user", "zwykły tekst"));

        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.String, content.ValueKind);
        Assert.Equal("zwykły tekst", content.GetString());
    }
}

public sealed class ChatAttachmentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "imagestudio-tests-" + Guid.NewGuid().ToString("N"));

    public ChatAttachmentTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File_(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static (ChatViewModel Vm, FakeProvider Provider, FakeDialogs Dialogs, List<(string Text, bool IsError)> Reports) Create(
        Func<string, bool?>? acceptsImage = null, int maxSide = 1536)
    {
        var provider = new FakeProvider { OnChat = (_, _) => "widzę obraz" };
        var dialogs = new FakeDialogs();
        var reports = new List<(string, bool)>();
        var vm = new ChatViewModel(() => provider, ["m1"], (text, error) => reports.Add((text, error)), "", null, null,
                                   new ChatImageSupport(dialogs, acceptsImage ?? (_ => null), () => maxSide))
        {
            SelectedModel = "m1",
        };
        return (vm, provider, dialogs, reports);
    }

    [Fact]
    public void Picked_files_become_attachments_and_other_file_types_are_skipped()
    {
        var (vm, _, dialogs, _) = Create();
        dialogs.PickResult = [File_("a.png", TestImages.Png(20, 20)), File_("b.jpg", TestImages.Jpeg(20, 20)), File_("notatki.txt", [1, 2, 3])];

        vm.AttachImagesCommand.Execute(null);

        Assert.Equal(["image/png", "image/jpeg"], vm.Attachments.Select(a => a.Mime));
    }

    [Fact]
    public void Cancelled_picker_adds_nothing()
    {
        var (vm, _, dialogs, _) = Create();
        dialogs.PickResult = null;

        vm.AttachImagesCommand.Execute(null);

        Assert.Empty(vm.Attachments);
    }

    [Fact]
    public void Large_images_are_downscaled_to_the_configured_limit()
    {
        var (vm, _, dialogs, _) = Create(maxSide: 100);
        dialogs.PickResult = [File_("duzy.png", TestImages.Png(400, 200))];

        vm.AttachImagesCommand.Execute(null);

        Assert.Equal((100, 50), TestImages.Size(Assert.Single(vm.Attachments).Bytes));
    }

    [Fact]
    public void Broken_image_file_is_reported_and_not_attached()
    {
        var (vm, _, _, reports) = Create();

        vm.AddFiles([File_("zepsuty.png", [1, 2, 3, 4, 5, 6])]);

        Assert.Empty(vm.Attachments);
        Assert.True(reports[^1].IsError);
        Assert.Contains("zepsuty.png", reports[^1].Text);
    }

    [Fact]
    public void Paste_adds_the_clipboard_image_as_png()
    {
        var (vm, _, dialogs, _) = Create();
        dialogs.Clipboard = TestImages.Bitmap(30, 20);

        vm.PasteImageCommand.Execute(null);

        var image = Assert.Single(vm.Attachments);
        Assert.Equal("image/png", image.Mime);
        Assert.Equal((30, 20), TestImages.Size(image.Bytes));
    }

    [Fact]
    public void Paste_without_an_image_reports_it()
    {
        var (vm, _, dialogs, reports) = Create();
        dialogs.Clipboard = null;

        vm.PasteImageCommand.Execute(null);

        Assert.Empty(vm.Attachments);
        Assert.Equal("Schowek nie zawiera obrazu.", reports[^1].Text);
    }

    [Fact]
    public void Attachment_can_be_removed()
    {
        var (vm, _, dialogs, _) = Create();
        dialogs.Clipboard = TestImages.Bitmap(10, 10);
        vm.PasteImageCommand.Execute(null);

        vm.RemoveAttachmentCommand.Execute(vm.Attachments[0]);

        Assert.Empty(vm.Attachments);
    }

    [Fact]
    public async Task Send_includes_images_and_clears_the_attachments()
    {
        var (vm, provider, dialogs, _) = Create();
        dialogs.Clipboard = TestImages.Bitmap(10, 10);
        vm.PasteImageCommand.Execute(null);
        vm.Input = "co na zdjęciu?";

        await vm.SendCommand.ExecuteAsync(null);

        var sent = provider.LastMessages.First(m => m.Role == "user");
        Assert.Equal("co na zdjęciu?", sent.Content);
        Assert.Single(sent.Images!);
        Assert.Empty(vm.Attachments);
        Assert.Same(sent.Images![0], vm.Messages[0].Images![0]);
        Assert.Equal("widzę obraz", vm.Messages[^1].Content);
    }

    [Fact]
    public async Task Image_without_text_can_be_sent()
    {
        var (vm, provider, dialogs, _) = Create();
        dialogs.Clipboard = TestImages.Bitmap(10, 10);
        vm.PasteImageCommand.Execute(null);
        vm.Input = "";

        Assert.True(vm.SendCommand.CanExecute(null));
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("", provider.LastMessages.First(m => m.Role == "user").Content);
        Assert.Equal(2, vm.Messages.Count);
    }

    [Fact]
    public void Nothing_to_send_without_text_and_images()
    {
        var (vm, _, _, _) = Create();
        vm.Input = "  ";

        Assert.False(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task Failed_send_restores_text_and_images()
    {
        var (vm, provider, dialogs, _) = Create();
        provider.OnChat = (_, _) => throw new InvalidOperationException("HTTP 400");
        dialogs.Clipboard = TestImages.Bitmap(10, 10);
        vm.PasteImageCommand.Execute(null);
        var image = vm.Attachments[0];
        vm.Input = "pytanie";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Empty(vm.Messages);
        Assert.Equal("pytanie", vm.Input);
        Assert.Same(image, Assert.Single(vm.Attachments));
    }

    [Fact]
    public void Warning_appears_only_for_models_that_do_not_take_images()
    {
        var (vm, _, dialogs, _) = Create(model => model == "m1" ? false : null);
        Assert.Equal("", vm.ImageWarning);

        dialogs.Clipboard = TestImages.Bitmap(10, 10);
        vm.PasteImageCommand.Execute(null);
        Assert.Contains("nie przyjmuje obrazów", vm.ImageWarning);

        vm.Attachments.Clear();
        Assert.Equal("", vm.ImageWarning);
    }

    [Fact]
    public void Warning_is_empty_when_the_catalog_does_not_say_or_model_accepts_images()
    {
        var (vm, _, dialogs, _) = Create(_ => null);
        dialogs.Clipboard = TestImages.Bitmap(10, 10);
        vm.PasteImageCommand.Execute(null);
        Assert.Equal("", vm.ImageWarning);

        var (vm2, _, dialogs2, _) = Create(_ => true);
        dialogs2.Clipboard = TestImages.Bitmap(10, 10);
        vm2.PasteImageCommand.Execute(null);
        Assert.Equal("", vm2.ImageWarning);
    }

    [Fact]
    public void Attaching_is_unavailable_without_image_support()
    {
        var vm = new ChatViewModel(() => new FakeProvider(), ["m1"], (_, _) => { }, "") { SelectedModel = "m1" };

        Assert.False(vm.AttachImagesCommand.CanExecute(null));
        Assert.False(vm.PasteImageCommand.CanExecute(null));
    }
}
