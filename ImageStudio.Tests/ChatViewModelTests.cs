using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

public class ChatViewModelTests
{
    private static (ChatViewModel Vm, FakeProvider Provider, List<(string Text, bool IsError)> Reports) Create(string system = "")
    {
        var provider = new FakeProvider();
        var reports = new List<(string, bool)>();
        var vm = new ChatViewModel(() => provider, ["m1", "m2"], (text, isError) => reports.Add((text, isError)), system)
        {
            SelectedModel = "m1",
        };
        return (vm, provider, reports);
    }

    [Fact]
    public async Task Send_appends_user_and_assistant_messages()
    {
        var (vm, provider, _) = Create();
        provider.OnChat = (_, _) => "hej";
        vm.Input = "cześć";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal([new ChatMessage("user", "cześć"), new ChatMessage("assistant", "hej")], vm.Messages);
        Assert.Equal("", vm.Input);
        Assert.False(vm.IsSending);
        Assert.Equal("m1", provider.LastModel);
    }

    [Fact]
    public async Task Send_puts_system_prompt_first_and_keeps_full_history()
    {
        var (vm, provider, _) = Create(system: "  Bądź zwięzły  ");
        provider.OnChat = (_, _) => "ok";

        vm.Input = "pierwsze";
        await vm.SendCommand.ExecuteAsync(null);
        vm.Input = "drugie";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(["system", "user", "assistant", "user"], provider.LastMessages.Select(m => m.Role));
        Assert.Equal("Bądź zwięzły", provider.LastMessages[0].Content);
    }

    [Fact]
    public async Task Send_without_system_prompt_sends_no_system_message()
    {
        var (vm, provider, _) = Create(system: "   ");
        vm.Input = "pytanie";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(["user"], provider.LastMessages.Select(m => m.Role));
    }

    [Fact]
    public async Task Failed_send_restores_input_removes_message_and_reports_error()
    {
        var (vm, provider, reports) = Create();
        provider.OnChat = (_, _) => throw new InvalidOperationException("HTTP 503");
        vm.Input = "pytanie";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Empty(vm.Messages);
        Assert.Equal("pytanie", vm.Input);
        Assert.False(vm.IsSending);
        var last = reports[^1];
        Assert.True(last.IsError);
        Assert.Contains("Błąd czatu", last.Text);
        Assert.Contains("HTTP 503", last.Text);
    }

    [Fact]
    public void Cannot_send_without_model_or_text()
    {
        var (vm, _, _) = Create();

        vm.Input = "   ";
        Assert.False(vm.SendCommand.CanExecute(null));

        vm.Input = "tekst";
        Assert.True(vm.SendCommand.CanExecute(null));

        vm.SelectedModel = null;
        Assert.False(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task NewChat_clears_history()
    {
        var (vm, provider, _) = Create();
        provider.OnChat = (_, _) => "odp";
        vm.Input = "pytanie";
        await vm.SendCommand.ExecuteAsync(null);

        vm.NewChatCommand.Execute(null);

        Assert.Empty(vm.Messages);
    }

    [Fact]
    public void ChatMessage_labels_roles_for_the_conversation_view()
    {
        Assert.True(new ChatMessage("user", "x").IsUser);
        Assert.False(new ChatMessage("assistant", "x").IsUser);
        Assert.Equal("Ty", new ChatMessage("user", "x").RoleLabel);
        Assert.Equal("Model", new ChatMessage("assistant", "x").RoleLabel);
        Assert.Equal("System", new ChatMessage("system", "x").RoleLabel);
    }
}
