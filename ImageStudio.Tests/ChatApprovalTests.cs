using ImageStudio.Configuration;
using ImageStudio.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.ViewModels;
using ImageStudio.Views;

namespace ImageStudio.Tests;

// Zgoda na narzędzia pytana w oknie czatu (raz, w rozmowie, na stałe) i zwijanie długich wyników narzędzi
public sealed class ChatApprovalTests
{
    private static ToolCall Call(string id = "c1", string name = "fs__read") => new(id, name, """{"path":"a.txt"}""");

    private static (ChatViewModel Vm, FakeProvider Provider, FakeToolHost Host) Create(params ToolCall[] calls)
    {
        var provider = new FakeProvider
        {
            OnReply = history => history[^1].Role == "tool" ? new ChatReply("gotowe", []) : new ChatReply("", calls),
        };
        var host = new FakeToolHost { RequireApproval = true };
        var vm = new ChatViewModel(() => provider, ["m1"], (_, _) => { }, "", host) { SelectedModel = "m1", InlineApproval = true };
        return (vm, provider, host);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private static async Task<Task> SendAndWaitForPrompt(ChatViewModel vm, string text = "czytaj")
    {
        vm.Input = text;
        var send = vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.PendingApproval is not null || send.IsCompleted);
        return send;
    }

    [Fact]
    public async Task The_chat_asks_in_its_own_window_and_runs_the_tool_after_one_time_consent()
    {
        var (vm, _, host) = Create(Call());

        var send = await SendAndWaitForPrompt(vm);

        Assert.Equal("fs__read", vm.PendingApproval!.Call.Name);
        Assert.Empty(host.Calls);
        vm.AnswerApprovalCommand.Execute("Once");
        await send;

        Assert.Single(host.Calls);
        Assert.Null(vm.PendingApproval);
        Assert.False(vm.Permissions.IsAllowed("fs__read"));
    }

    [Fact]
    public async Task Denying_does_not_run_the_tool_and_the_model_is_told()
    {
        var (vm, _, host) = Create(Call());

        var send = await SendAndWaitForPrompt(vm);
        vm.AnswerApprovalCommand.Execute("Deny");
        await send;

        Assert.Empty(host.Calls);
        Assert.Contains("odrzucił", vm.Messages.First(m => m.Role == "tool").Content);
        Assert.False(vm.Permissions.IsAllowed("fs__read"));
    }

    [Fact]
    public async Task Session_consent_skips_the_question_until_a_new_chat_starts()
    {
        var (vm, _, host) = Create(Call());

        var send = await SendAndWaitForPrompt(vm);
        vm.AnswerApprovalCommand.Execute("Session");
        await send;
        Assert.True(vm.Permissions.IsAllowed("fs__read"));

        // to samo narzędzie w kolejnej wiadomości: bez pytania
        vm.Input = "jeszcze raz";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Null(vm.PendingApproval);
        Assert.Equal(2, host.Calls.Count);

        vm.NewChatCommand.Execute(null);
        Assert.False(vm.Permissions.IsAllowed("fs__read"));
    }

    [Fact]
    public async Task Permanent_consent_survives_a_new_chat_and_other_tools_still_ask()
    {
        var (vm, _, host) = Create(Call());

        var send = await SendAndWaitForPrompt(vm);
        vm.AnswerApprovalCommand.Execute("Always");
        await send;
        vm.NewChatCommand.Execute(null);

        Assert.True(vm.Permissions.IsAllowed("fs__read"));
        Assert.Equal(["fs__read"], vm.Permissions.Always);
        Assert.False(vm.Permissions.IsAllowed("fs__write"));
    }

    [Fact]
    public async Task Stop_while_waiting_for_consent_cancels_the_question()
    {
        var (vm, _, host) = Create(Call());

        var send = await SendAndWaitForPrompt(vm);
        Assert.NotNull(vm.PendingApproval);
        vm.StopCommand.Execute(null);
        await send;

        Assert.Null(vm.PendingApproval);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Without_inline_approval_the_confirm_callback_still_decides()
    {
        var provider = new FakeProvider { OnReply = h => h.Any(m => m.Role == "tool") ? new ChatReply("ok", []) : new ChatReply("", [Call()]) };
        var host = new FakeToolHost { RequireApproval = true };
        var asked = 0;
        var vm = new ChatViewModel(() => provider, ["m1"], (_, _) => { }, "", host, _ => { asked++; return true; }) { SelectedModel = "m1" };
        vm.Permissions.Allow("fs__read", ApprovalChoice.Session);
        vm.Input = "x";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(0, asked);   // zapamiętana zgoda wyprzedza pytanie
        Assert.Single(host.Calls);
    }

    [Fact]
    public async Task Permissions_are_saved_in_state_and_can_be_forgotten_from_the_tools_panel()
    {
        using var app = TestApp.Create();
        await app.Vm.InitializeAsync();

        app.Vm.Tools.Permissions.Allow("builtin__files_write", ApprovalChoice.Always);
        Assert.Equal(["builtin__files_write"], app.Get<IUserStateStore>().Load().AlwaysAllowedTools);
        Assert.Contains("1", app.Vm.Tools.PermissionsSummary);
        Assert.Same(app.Vm.Tools.Permissions, app.Vm.Chat.Permissions);
        Assert.True(app.Vm.Chat.InlineApproval);

        app.Vm.Tools.ForgetPermissionsCommand.Execute(null);
        Assert.Empty(app.Get<IUserStateStore>().Load().AlwaysAllowedTools);
        Assert.False(app.Vm.Chat.Permissions.IsAllowed("builtin__files_write"));
    }

    private static string Long() => new('x', ChatMessage.LongLength + 10);

    private static async Task SendWithResult(ChatViewModel vm, FakeToolHost host, string result)
    {
        host.RequireApproval = false;
        host.OnCall = _ => result;
        vm.Input = "daj";
        await vm.SendCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task A_long_tool_result_stays_expanded_while_asking_and_can_be_toggled()
    {
        var (vm, _, host) = Create(Call());
        await SendWithResult(vm, host, Long());

        var tool = vm.Messages.Single(m => m.Role == "tool");
        Assert.True(tool.IsLong);
        Assert.True(vm.AskCollapse);
        Assert.False(tool.Collapsed);

        vm.ToggleCollapseCommand.Execute(tool);
        Assert.True(tool.Collapsed);
        vm.ToggleCollapseCommand.Execute(tool);
        Assert.False(tool.Collapsed);
    }

    [Fact]
    public async Task Always_collapse_sets_the_flag_collapses_existing_results_and_stops_asking()
    {
        var (vm, _, host) = Create(Call());
        await SendWithResult(vm, host, Long());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.CollapseAlwaysCommand.Execute(null);

        Assert.True(vm.CollapseLong);
        Assert.False(vm.AskCollapse);
        Assert.Contains("CollapseLong", raised);
        Assert.True(vm.Messages.Single(m => m.Role == "tool").Collapsed);

        // kolejny długi wynik jest zwinięty od razu, krótki zostaje rozwinięty
        await SendWithResult(vm, host, Long());
        await SendWithResult(vm, host, "krótko");
        var results = vm.Messages.Where(m => m.Role == "tool").ToList();
        Assert.True(results[1].Collapsed);
        Assert.False(results[2].IsLong);
        Assert.False(results[2].Collapsed);
    }

    [Fact]
    public async Task The_collapse_flag_is_saved_in_state_and_applied_to_a_restored_chat()
    {
        var memory = new MemoryUserStateStore();
        memory.State.CollapseLongResults = true;
        using var app = TestApp.Create(s => Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IUserStateStore>(s, memory));

        Assert.True(app.Vm.Chat.CollapseLong);

        await app.Vm.InitializeAsync();
        app.Vm.Chat.CollapseLong = null;
        Assert.Null(memory.State.CollapseLongResults);
        app.Vm.Chat.CollapseAlwaysCommand.Execute(null);
        Assert.True(memory.State.CollapseLongResults);
    }

    private static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in All<T>(child)) yield return nested;
        }
    }

    [Fact]
    public void The_view_shows_the_consent_panel_only_while_asking_and_limits_a_collapsed_result()
    {
        WpfHost.Run(() =>
        {
            var (vm, _, _) = Create();
            var tool = new ChatMessage("tool", Long(), ToolCallId: "c", ToolName: "fs__read");
            vm.Messages.Add(tool);
            var window = new Window { Content = new ChatConversation { DataContext = vm }, ShowInTaskbar = false, Width = 700, Height = 600 };
            window.Show();
            void Idle() => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Idle();

            Border Panel() => All<Border>(window).First(b => b.Child is StackPanel { Children.Count: 3 } sp && sp.Children[1] is TextBox);
            Assert.Equal(Visibility.Collapsed, Panel().Visibility);

            vm.PendingApproval = new ToolApproval(Call());
            Idle();
            Assert.Equal(Visibility.Visible, Panel().Visibility);
            vm.PendingApproval = null;

            var text = All<TextBox>(window).First(t => t.Text == tool.Content);
            Assert.True(double.IsPositiveInfinity(text.MaxHeight));
            vm.ToggleCollapseCommand.Execute(tool);
            Idle();
            Assert.Equal(64, text.MaxHeight);
            window.Close();
        });
    }
}
