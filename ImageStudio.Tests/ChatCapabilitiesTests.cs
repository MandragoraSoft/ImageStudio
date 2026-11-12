using System.Windows;
using System.Windows.Media;
using System.Net;
using System.Text;
using System.Text.Json;
using ImageStudio.Services;
using ImageStudio.Tabs.Chat;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

public sealed class ChatCapabilitiesTests
{
    [Fact]
    public async Task Uber_text_only_model_sends_streaming_chat_without_tool_definitions()
    {
        string? requestBody = null;
        using var provider = new UberLlmProvider("https://example.test", "", "https://example.test",
            () => new CatalogAndChatHandler(body => requestBody = body));
        var catalog = await provider.GetChatModelsAsync();
        var chat = new ChatViewModel(() => provider, ["venice/uncensored"], (_, _) => { }, "", new FakeToolHost(),
            modelInfo: id => catalog.FirstOrDefault(model => model.Id == id), useStreaming: () => true)
            { SelectedModel = "venice/uncensored", Input = "Cześć" };

        await chat.SendCommand.ExecuteAsync(null);

        using var json = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        Assert.Equal("venice/uncensored", json.RootElement.GetProperty("model").GetString());
        Assert.True(json.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("tools", out _));
        Assert.Equal("Cześć", json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("Witaj", chat.Messages.Last().Content);
    }

    private sealed class CatalogAndChatHandler(Action<string> capture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"data":[{"id":"venice/uncensored","available":true,"endpoints":["/v1/chat/completions"],"supports_tools":false,"input_modalities":["text"]}]}
                        """, Encoding.UTF8, "application/json")
                };
            capture(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"Witaj\"}}]}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8, "text/event-stream")
            };
        }
    }

    [Fact]
    public async Task Switching_models_restores_tools_and_original_history()
    {
        var host = new FakeToolHost();
        var provider = new FakeProvider { OnChat = (_, _) => "ok" };
        var support = new Dictionary<string, bool?> { ["yes"] = true, ["no"] = false, ["unknown"] = null };
        var vm = new ChatViewModel(() => provider, ["yes", "no", "unknown"], (_, _) => { }, "", host,
            modelInfo: id => new ChatModelInfo(id, []) { SupportsTools = support.GetValueOrDefault(id) });
        var call = new ToolCall("id", "fs__read", "{}");
        vm.Messages.Add(new ChatMessage("assistant", "przed", [call]));
        vm.Messages.Add(new ChatMessage("tool", "wynik", ToolCallId: "id"));
        foreach (var model in new[] { "yes", "no", "unknown", "yes" })
        {
            vm.SelectedModel = model;
            vm.Input = "dalej";
            await vm.SendCommand.ExecuteAsync(null);
            Assert.Equal(model == "yes", provider.LastTools is not null);
            Assert.Equal(model == "yes", provider.LastMessages.Any(m => m.Role == "tool"));
            Assert.Equal(model == "yes", vm.SupportsTools);
        }
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Unsupported_models_preserve_history_as_text_without_running_tools(bool? support)
    {
        var host = new FakeToolHost();
        var provider = new FakeProvider { OnChat = (_, _) => "odpowiedź" };
        var vm = new ChatViewModel(() => provider, ["m"], (_, _) => { }, "", host,
            modelInfo: id => new ChatModelInfo(id, []) { SupportsTools = support }) { SelectedModel = "m" };
        var call = new ToolCall("id", "fs__read", "{\"path\":\"plik\"}");
        var assistant = new ChatMessage("assistant", "treść", [call]);
        var result = new ChatMessage("tool", "wynik", ToolCallId: "id", ToolName: "fs__read");
        vm.Messages.Add(assistant);
        vm.Messages.Add(result);
        vm.Input = "dalej";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Null(provider.LastTools);
        Assert.Empty(host.Calls);
        Assert.DoesNotContain(provider.LastMessages, m => m.Role == "tool" || m.HasToolCalls);
        Assert.Contains(provider.LastMessages, m => m.Content.Contains("fs__read") && m.Content.Contains("plik"));
        Assert.Contains(provider.LastMessages, m => m.Content.Contains("wynik") && m.Content.Contains("id"));
        Assert.Same(assistant, vm.Messages[0]);
        Assert.Same(result, vm.Messages[1]);
        Assert.True(assistant.HasToolCalls);
        Assert.Equal("tool", result.Role);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(null, true)]
    public async Task Unexpected_tool_call_is_rejected_even_when_streamed(bool? support, bool stream)
    {
        var host = new FakeToolHost();
        var provider = new FakeProvider { OnReply = _ => new ChatReply("tekst", [new ToolCall("id", "fs__read", "{}")]) };
        var reports = new List<(string Text, bool Error)>();
        var vm = new ChatViewModel(() => provider, ["m"], (text, error) => reports.Add((text, error)), "", host,
            modelInfo: id => new ChatModelInfo(id, []) { SupportsTools = support }, useStreaming: () => stream)
            { SelectedModel = "m", Input = "pytanie" };

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Empty(host.Calls);
        Assert.Empty(vm.Messages);
        Assert.Equal("pytanie", vm.Input);
        Assert.Contains(reports, r => r.Error && r.Text.Contains("odrzucono"));
    }

    [Fact]
    public async Task Catalog_change_while_waiting_rejects_returned_tool_call()
    {
        var host = new FakeToolHost();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider
        {
            Gate = _ => gate.Task,
            OnReply = _ => new ChatReply("", [new ToolCall("id", "fs__read", "{}")])
        };
        bool? support = true;
        var vm = new ChatViewModel(() => provider, ["m"], (_, _) => { }, "", host,
            modelInfo: id => new ChatModelInfo(id, []) { SupportsTools = support }) { SelectedModel = "m", Input = "tekst" };
        var send = vm.SendCommand.ExecuteAsync(null);
        support = false;
        vm.RefreshReasoningSupport();
        gate.SetResult();
        await send;
        Assert.Empty(host.Calls);
        Assert.Equal("tekst", vm.Input);
    }

    [Fact]
    public async Task Catalog_change_while_awaiting_approval_does_not_run_the_tool()
    {
        var host = new FakeToolHost { RequireApproval = true };
        var provider = new FakeProvider { OnReply = _ => new ChatReply("", [new ToolCall("id", "fs__read", "{}")]) };
        bool? support = true;
        var reports = new List<(string Text, bool Error)>();
        var vm = new ChatViewModel(() => provider, ["m"], (text, error) => reports.Add((text, error)), "", host,
            modelInfo: id => new ChatModelInfo(id, []) { SupportsTools = support })
            { SelectedModel = "m", Input = "tekst", InlineApproval = true };
        var send = vm.SendCommand.ExecuteAsync(null);
        Assert.NotNull(vm.PendingApproval);
        support = false;
        vm.AnswerApprovalCommand.Execute("Once");
        await send;
        Assert.Empty(host.Calls);
        Assert.Contains(reports, r => r.Error && r.Text.Contains("odrzucono"));
        Assert.Single(vm.Messages, m => m.Role == "tool");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    [InlineData(true)]
    public async Task Image_capability_blocks_only_explicit_false_and_preserves_draft(bool? accepts)
    {
        var provider = new FakeProvider { OnChat = (_, _) => "ok" };
        var reports = new List<(string Text, bool Error)>();
        var vm = new ChatViewModel(() => provider, ["m"], (text, error) => reports.Add((text, error)), "",
            images: new ChatImageSupport(new FakeDialogs(), _ => accepts, () => 1536)) { SelectedModel = "m", Input = "tekst" };
        var image = new ChatImage([1, 2, 3], "image/png");
        vm.Attachments.Add(image);
        Assert.Equal(accepts != false, vm.SendCommand.CanExecute(null));

        await vm.SendCommand.ExecuteAsync(null);

        if (accepts == false)
        {
            Assert.Equal(0, provider.ChatCalls);
            Assert.Equal("tekst", vm.Input);
            Assert.Same(image, Assert.Single(vm.Attachments));
            Assert.Empty(vm.Messages);
            Assert.Contains(reports, r => r.Error && r.Text.Contains("obraz"));
        }
        else
        {
            Assert.Equal(1, provider.ChatCalls);
            Assert.Single(provider.LastMessages.Last().Images!);
        }
    }

    [Fact]
    public void Execute_ignores_disabled_command_without_clearing_draft()
    {
        var provider = new FakeProvider();
        var vm = new ChatViewModel(() => provider, ["m"], (_, _) => { }, "",
            images: new ChatImageSupport(new FakeDialogs(), _ => false, () => 1536)) { SelectedModel = "m", Input = "tekst" };
        var image = new ChatImage([1], "image/png");
        vm.Attachments.Add(image);
        Assert.False(vm.SendCommand.CanExecute(null));
        vm.SendCommand.Execute(null);
        Assert.Equal("tekst", vm.Input);
        Assert.Same(image, Assert.Single(vm.Attachments));
        Assert.Equal(0, provider.ChatCalls);
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Find<T>(child)) yield return nested;
        }
    }

    [Fact]
    public async Task Catalog_reload_updates_both_conversations_and_panel_without_changing_tools()
    {
        await WpfHost.RunAsync(async () =>
        {
            var provider = new FakeProvider { ChatCatalog = [new ChatModelInfo("m", [], true) { SupportsTools = true }], OnChat = (_, _) => "ok" };
            var dialogs = new FakeDialogs();
            var vm = TestApp.CreateVm(provider, dialogs);
            await vm.InitializeAsync();
            vm.EditGeneratePromptCommand.Execute(null);
            var prompt = dialogs.LastConversationFactory!(() => dialogs.LastInitial!, (_, _) => { });
            prompt.SelectedModel = "m";
            vm.Chat.SelectedModel = "m";
            vm.Tools.UseTools = true;
            prompt.Input = "początek";
            await prompt.SendCommand.ExecuteAsync(null);
            Assert.NotNull(provider.LastTools);
            prompt.Messages.Add(new ChatMessage("assistant", "przed", [new ToolCall("id", "fs__read", "{}")]));
            prompt.Messages.Add(new ChatMessage("tool", "wynik", ToolCallId: "id"));
            var panel = new ChatPanel { DataContext = vm.Chat };
            var window = new Window { Content = panel };
            window.Show();
            window.UpdateLayout();
            var toolsPanel = Assert.Single(Find<ToolsPanel>(panel));
            var useTools = vm.Tools.UseTools;
            var disabled = vm.Tools.DisabledTools.ToArray();
            var toolConfigurationChanges = 0;
            vm.Tools.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ToolsViewModel.DisabledTools) or nameof(ToolsViewModel.UseTools))
                    toolConfigurationChanges++;
            };
            Assert.True(toolsPanel.IsEnabled);

            provider.ChatCatalog = [new ChatModelInfo("m", [], false) { SupportsTools = false }];
            await vm.ReloadModelsCommand.ExecuteAsync(null);
            Assert.False(toolsPanel.IsEnabled);
            Assert.False(prompt.SupportsTools);
            Assert.NotEmpty(vm.Chat.ToolsWarning);
            prompt.Input = "tekst";
            await prompt.SendCommand.ExecuteAsync(null);
            Assert.Null(provider.LastTools);
            Assert.DoesNotContain(provider.LastMessages, m => m.Role == "tool" || m.HasToolCalls);
            Assert.Contains(provider.LastMessages, m => m.Content.Contains("wynik"));
            var image = new ChatImage([1], "image/png");
            prompt.Attachments.Add(image);
            prompt.Input = "szkic";
            Assert.False(prompt.SendCommand.CanExecute(null));
            await prompt.SendCommand.ExecuteAsync(null);
            Assert.Same(image, Assert.Single(prompt.Attachments));
            Assert.Equal("szkic", prompt.Input);
            Assert.Equal(useTools, vm.Tools.UseTools);
            Assert.Equal(disabled, vm.Tools.DisabledTools);

            provider.ChatCatalog = [new ChatModelInfo("m", [])];
            await vm.ReloadModelsCommand.ExecuteAsync(null);
            Assert.False(toolsPanel.IsEnabled);
            Assert.False(prompt.SupportsTools);
            prompt.Attachments.Clear();
            await prompt.SendCommand.ExecuteAsync(null);
            Assert.Null(provider.LastTools);

            provider.ChatCatalog = [new ChatModelInfo("m", []) { SupportsTools = true }];
            await vm.ReloadModelsCommand.ExecuteAsync(null);
            Assert.True(toolsPanel.IsEnabled);
            Assert.True(prompt.SupportsTools);
            prompt.Input = "dalej";
            Assert.True(prompt.SendCommand.CanExecute(null));
            await prompt.SendCommand.ExecuteAsync(null);
            Assert.NotNull(provider.LastTools);
            Assert.Contains(provider.LastMessages, m => m.Role == "tool" && m.Content == "wynik");
            Assert.Equal(useTools, vm.Tools.UseTools);
            Assert.Equal(disabled, vm.Tools.DisabledTools);
            Assert.Equal(0, toolConfigurationChanges);
            window.Close();
        });
    }
}
