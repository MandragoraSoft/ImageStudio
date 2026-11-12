using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

public sealed class DescribeImagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "is-describe-" + Guid.NewGuid().ToString("N"));

    public DescribeImagesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private string Png(string name, int size = 8)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, TestImages.Png(size, size));
        return path;
    }

    private static (MainViewModel Vm, FakeDialogs Dialogs, FakeProvider Provider) Create()
    {
        var dialogs = new FakeDialogs();
        var provider = new FakeProvider { OnChat = (_, _) => "  Kot siedzi na parapecie.  " };
        var vm = TestApp.CreateVm(provider, dialogs);
        vm.SelectedEnhancerModel = "vision-model";
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

    [Fact]
    public void Conversation_analyzes_tab_and_chat_images_without_changing_prompt_or_draft()
    {
        Sta.Run(() =>
        {
            var (vm, dialogs, provider) = Create();
            var path = Png("tab.png");
            vm.References.Add(new ReferenceItem(path, "a", "a"));
            vm.EditPrompt = "oryginalny prompt";
            vm.EditEditPromptCommand.Execute(null);
            var chat = dialogs.LastConversationFactory!(() => dialogs.LastInitial!, (_, _) => { });
            chat.SelectedModel = "model-rozmowy";
            var duplicate = new ChatImage(File.ReadAllBytes(path), "image/png");
            chat.Attachments.Add(duplicate);
            var pending = new ChatImage(TestImages.Png(10, 10), "image/png");
            chat.Attachments.Add(pending);
            var historical = new ChatImage(TestImages.Png(9, 9), "image/png");
            chat.Messages.Add(new ChatMessage("user", "wcześniej", Images: [historical]));
            chat.Input = "niewysłana wiadomość";

            chat.AnalyzeImagesCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Equal("model-rozmowy", provider.LastModel);
            var analyzed = provider.LastMessages.Last().Images!;
            Assert.Equal(2, analyzed.Count);
            Assert.DoesNotContain(analyzed, image => image.Bytes.SequenceEqual(historical.Bytes));
            Assert.Contains(analyzed, image => image.Bytes.SequenceEqual(pending.Bytes));
            Assert.Equal("niewysłana wiadomość", chat.Input);
            Assert.Equal([duplicate, pending], chat.Attachments);
            Assert.Equal("oryginalny prompt", vm.EditPrompt);
            Assert.Contains(chat.Messages, m => m.Role == "assistant" && m.Content.Contains("Kot siedzi"));

            chat.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Contains(provider.LastMessages, m => m.Role == "assistant" && m.Content.Contains("Kot siedzi"));
        });
    }

    [Fact]
    public void Repeated_analysis_uses_only_current_edit_references()
    {
        Sta.Run(() =>
        {
            var (vm, dialogs, provider) = Create();
            var first = Png("first.png");
            var second = Png("second.png", 9);
            vm.References.Add(new ReferenceItem(first, "a", "a"));
            vm.EditEditPromptCommand.Execute(null);
            var chat = dialogs.LastConversationFactory!(() => dialogs.LastInitial!, (_, _) => { });

            chat.AnalyzeImagesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Equal(File.ReadAllBytes(first), Assert.Single(provider.LastMessages.Last().Images!).Bytes);
            vm.References.Clear();
            vm.References.Add(new ReferenceItem(second, "b", "b"));
            vm.EditEditPromptCommand.Execute(null);
            chat = dialogs.LastConversationFactory!(() => dialogs.LastInitial!, (_, _) => { });

            chat.AnalyzeImagesCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Equal(File.ReadAllBytes(second), Assert.Single(provider.LastMessages.Last().Images!).Bytes);
            Assert.Contains(chat.Messages, m => m.Images?.Any(i => i.Bytes.SequenceEqual(File.ReadAllBytes(first))) == true);
            Assert.Single(provider.LastMessages, m => m.HasImages);
        });
    }

    [Fact]
    public void Analysis_with_only_historical_images_asks_for_new_files()
    {
        Sta.Run(() =>
        {
            var (vm, dialogs, provider) = Create();
            vm.References.Add(new ReferenceItem(Png("first.png"), "a", "a"));
            vm.EditEditPromptCommand.Execute(null);
            var chat = dialogs.LastConversationFactory!(() => dialogs.LastInitial!, (_, _) => { });
            chat.AnalyzeImagesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            vm.References.Clear();

            dialogs.PickResult = null;
            chat.AnalyzeImagesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Equal(1, provider.ChatCalls);

            var selected = Png("selected.png", 9);
            dialogs.PickResult = [selected];
            chat.AnalyzeImagesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Equal(2, provider.ChatCalls);
            Assert.Equal(File.ReadAllBytes(selected), Assert.Single(provider.LastMessages.Last().Images!).Bytes);
        });
    }

    [Fact]
    public void Conversation_picks_files_when_no_images_and_preserves_input_on_failure()
    {
        Sta.Run(() =>
        {
            var (vm, dialogs, provider) = Create();
            vm.EditGeneratePromptCommand.Execute(null);
            var reports = new List<(string, bool)>();
            var chat = dialogs.LastConversationFactory!(() => dialogs.LastInitial!, (text, error) => reports.Add((text, error)));
            chat.Input = "szkic";
            dialogs.PickResult = null;
            chat.AnalyzeImagesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Equal(0, provider.ChatCalls);

            dialogs.PickResult = [Png("selected.png")];
            provider.OnChat = (_, _) => throw new InvalidOperationException("błąd modelu");
            chat.AnalyzeImagesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Empty(chat.Messages);
            Assert.Equal("szkic", chat.Input);
            Assert.Contains(reports, report => report.Item2 && report.Item1.Contains("błąd modelu"));
        });
    }

    [Fact]
    public void Video_editor_supplies_its_attachments_to_conversation_analysis()
    {
        Sta.Run(() =>
        {
            var (vm, dialogs, provider) = Create();
            var path = Png("video.png");
            vm.VideoAttachments.Add(new ReferenceItem(path, "v", "v"));
            vm.EditVideoPromptCommand.Execute(null);
            var chat = dialogs.LastConversationFactory!(() => dialogs.LastInitial!, (_, _) => { });
            chat.AnalyzeImagesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Single(provider.LastMessages.Last().Images!);
        });
    }

    [Fact]
    public async Task Stopping_conversation_analysis_keeps_draft_and_attachments()
    {
        await WpfHost.RunAsync(async () =>
        {
            var (vm, dialogs, provider) = Create();
            vm.EditGeneratePromptCommand.Execute(null);
            var chat = dialogs.LastConversationFactory!(() => dialogs.LastInitial!, (_, _) => { });
            var image = new ChatImage(TestImages.Png(8, 8), "image/png");
            chat.Attachments.Add(image);
            chat.Input = "szkic";
            provider.Gate = ct => Task.Delay(Timeout.Infinite, ct);

            var task = chat.AnalyzeImagesCommand.ExecuteAsync(null);
            Assert.True(chat.IsSending);
            chat.StopCommand.Execute(null);
            await task;
            Assert.False(chat.IsSending);
            Assert.Empty(chat.Messages);
            Assert.Equal("szkic", chat.Input);
            Assert.Same(image, Assert.Single(chat.Attachments));
        });
    }

    [Fact]
    public void Unsupported_vision_model_is_rejected_before_selecting_or_sending_images()
    {
        Sta.Run(() =>
        {
            var provider = new FakeProvider();
            var dialogs = new FakeDialogs();
            var reports = new List<(string, bool)>();
            var chat = new ChatViewModel(() => provider, ["m"], (text, error) => reports.Add((text, error)), "",
                images: new ChatImageSupport(dialogs, _ => false, () => 1536), analysisImagePaths: () => [])
                { SelectedModel = "m" };
            chat.AnalyzeImagesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Equal(0, provider.ChatCalls);
            Assert.Contains(reports, report => report.Item2);
        });
    }

    [Fact]
    public void Missing_tab_image_reports_error_without_sending_or_losing_draft()
    {
        Sta.Run(() =>
        {
            var (vm, dialogs, provider) = Create();
            vm.References.Add(new ReferenceItem(Path.Combine(_dir, "missing.png"), "a", "a"));
            vm.EditEditPromptCommand.Execute(null);
            var reports = new List<(string, bool)>();
            var chat = dialogs.LastConversationFactory!(() => dialogs.LastInitial!, (text, error) => reports.Add((text, error)));
            var image = new ChatImage(TestImages.Png(8, 8), "image/png");
            chat.Attachments.Add(image);
            chat.Input = "szkic";
            chat.AnalyzeImagesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Equal(0, provider.ChatCalls);
            Assert.Contains(reports, report => report.Item2);
            Assert.Equal("szkic", chat.Input);
            Assert.Same(image, Assert.Single(chat.Attachments));
        });
    }

    [Fact]
    public void Partial_analysis_is_not_left_in_context_on_stream_failure()
    {
        Sta.Run(() =>
        {
            var provider = new StreamingFakeProvider { FailAfterDeltas = new IOException("błąd strumienia") };
            provider.Deltas.Add(new ChatDelta("niepełny opis", null));
            var reports = new List<(string, bool)>();
            var chat = new ChatViewModel(() => provider, ["m"], (text, error) => reports.Add((text, error)), "",
                images: new ChatImageSupport(new FakeDialogs(), _ => true, () => 1536),
                useStreaming: () => true, analysisImagePaths: () => []) { SelectedModel = "m", Input = "szkic" };
            chat.Attachments.Add(new ChatImage(TestImages.Png(8, 8), "image/png"));
            chat.AnalyzeImagesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Empty(chat.Messages);
            Assert.Equal("szkic", chat.Input);
            Assert.Single(chat.Attachments);
            Assert.Contains(reports, report => report.Item2 && report.Item1.Contains("błąd strumienia"));
        });
    }
}
