using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using ImageStudio.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ImageStudio.Tests;

// Limity narzędzi MCP: konfiguracja (Ustawienia, zakładka MCP), zapis w state.json, ostrzeżenia o liczbie narzędzi i o uciętych listach
public sealed class McpLimitsTests
{
    [Fact]
    public void Defaults_are_30_tools_30_results_and_a_maximum_of_100()
    {
        var limits = new McpLimits();

        Assert.Equal((30, 30, 100), (limits.MaxTools, limits.ListLimit, limits.MaxListLimit));
        Assert.False(limits.ExceedsTools(30));
        Assert.True(limits.ExceedsTools(31));
    }

    [Theory]
    [InlineData(1, 0, 5, 5, 1, 10)]
    [InlineData(999, 9999, 5000, 500, 1000, 1000)]
    [InlineData(41, 50, 40, 41, 40, 40)]
    [InlineData(30, 30, 100, 30, 30, 100)]
    public void Values_are_clamped_and_the_default_list_never_exceeds_the_maximum(int tools, int list, int max, int expectedTools, int expectedList, int expectedMax)
    {
        Assert.Equal((expectedTools, expectedList, expectedMax), McpLimits.Normalize(tools, list, max));
    }

    [Fact]
    public void Requested_list_size_stays_within_the_configured_bounds()
    {
        var limits = new McpLimits();
        limits.Set(30, 20, 50);

        Assert.Equal(20, limits.ListSize(null));
        Assert.Equal(7, limits.ListSize(7));
        Assert.Equal(50, limits.ListSize(500));
        Assert.Equal(1, limits.ListSize(0));
    }

    [Fact]
    public void Limits_round_trip_through_the_state_file_and_changes_are_announced_once()
    {
        var state = new UserState();
        var limits = McpLimits.From(state);
        var changes = 0;
        limits.Changed += () => changes++;

        limits.Set(12, 8, 40);
        limits.Set(12, 8, 40);
        limits.Capture(state);

        Assert.Equal(1, changes);
        Assert.Equal((12, 8, 40), (state.McpMaxTools, state.McpListLimit, state.McpMaxListLimit));
        var again = McpLimits.From(state);
        Assert.Equal((12, 8, 40), (again.MaxTools, again.ListLimit, again.MaxListLimit));
    }

    [Fact]
    public void Paged_returns_a_plain_array_until_the_result_is_cut_and_then_warns()
    {
        var plain = McpToolArgs.Paged(new[] { 1, 2, 3 }, 3, 100);
        var cut = McpToolArgs.Paged(new[] { 1, 2, 3, 4 }, 3, 100);

        Assert.Equal("[1,2,3]", plain);
        using var json = JsonDocument.Parse(cut);
        Assert.Equal(3, json.RootElement.GetProperty("items").GetArrayLength());
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Contains("zakładka MCP", json.RootElement.GetProperty("warning").GetString());
        Assert.Contains("maks. 100", json.RootElement.GetProperty("warning").GetString());
    }

    private static StudioTools Tools(RecordingStudio studio, McpLimits limits) => new(studio, _ => true, limits);

    [Fact]
    public async Task List_tools_ask_for_one_more_row_than_the_limit_and_use_the_configured_default()
    {
        var studio = new RecordingStudio();
        var limits = new McpLimits();
        limits.Set(30, 12, 40);
        var tools = Tools(studio, limits);

        await tools.CallAsync("list_models", """{"kind":"edit"}""", false);
        await tools.CallAsync("list_models", """{"kind":"edit","limit":500}""", false);

        Assert.Equal(["models:edit::13", "models:edit::41"], studio.Requests);
    }

    [Fact]
    public async Task A_cut_gallery_list_carries_the_warning()
    {
        var library = new MemoryLibrary();
        for (var i = 0; i < 5; i++) library.MediaItems.Add(new GalleryEntry { Kind = "generate", Prompt = "p" + i, FilePath = "x" });
        var api = new StudioApi(() => new FakeProvider(), new Storage.Local.LocalMediaStorage(), library, new MemoryPresetStore());
        var limits = new McpLimits();
        limits.Set(30, 3, 10);

        var cut = await new StudioTools(api, _ => true, limits).CallAsync("list_gallery", "{}", false);
        var all = await new StudioTools(api, _ => true, limits).CallAsync("list_gallery", """{"limit":10}""", false);

        using var cutJson = JsonDocument.Parse(cut);
        Assert.Equal(3, cutJson.RootElement.GetProperty("items").GetArrayLength());
        Assert.True(cutJson.RootElement.GetProperty("truncated").GetBoolean());
        using var allJson = JsonDocument.Parse(all);
        Assert.Equal(JsonValueKind.Array, allJson.RootElement.ValueKind);
        Assert.Equal(5, allJson.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Chat_warns_when_more_tools_are_exposed_than_the_limit()
    {
        using var app = TestApp.Create();
        var tools = app.Vm.Tools;
        tools.UseTools = true;
        tools.AppControlEnabled = true;
        var count = tools.Tools.Count;

        Assert.True(count > 5);
        app.Vm.Limits.Set(count, 30, 100);
        Assert.Equal("", tools.LimitWarning);

        app.Vm.Limits.Set(count - 1, 30, 100);
        Assert.Contains($"{count} narzędzi", tools.LimitWarning);
        Assert.Contains($"{count - 1}", tools.LimitWarning);

        tools.UseTools = false;
        Assert.Equal("", tools.LimitWarning);
        await Task.CompletedTask;
    }

    [Fact]
    public void Changing_the_limit_notifies_the_warning_binding()
    {
        using var app = TestApp.Create();
        app.Vm.Tools.UseTools = true;
        var raised = new List<string?>();
        app.Vm.Tools.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        app.Vm.Limits.Set(6, 30, 100);

        Assert.Contains("LimitWarning", raised);
    }

    private static SettingsResult Current(int tools = 30, int list = 30, int max = 100) =>
        new("u", "m", 1536, "k", "dark", McpMaxTools: tools, McpListLimit: list, McpMaxListLimit: max);

    [Fact]
    public void Tab_starts_on_the_current_limits_and_clamps_what_it_returns()
    {
        var tab = new McpSettingsTab(Current(25, 10, 60));
        Assert.Equal((25, 10, 60), (tab.MaxTools, tab.ListLimit, tab.MaxListLimit));
        Assert.Equal(("MCP", 6), (tab.Title, tab.Order));

        tab.MaxTools = 1;
        tab.ListLimit = 900;
        tab.MaxListLimit = 5;
        var result = tab.Apply(Current() with { Theme = "light" });

        Assert.Equal((5, 10, 10), (result.McpMaxTools, result.McpListLimit, result.McpMaxListLimit));
        Assert.Equal("light", result.Theme);
    }

    [Fact]
    public async Task The_settings_dialog_offers_the_tab_and_the_limits_are_applied_and_saved()
    {
        using var app = TestApp.Create();
        await app.Vm.InitializeAsync();
        var dialogs = app.Get<FakeDialogs>();
        dialogs.SettingsAnswer = r => r with { McpMaxTools = 20, McpListLimit = 15, McpMaxListLimit = 80 };

        await app.Vm.OpenSettingsCommand.ExecuteAsync(null);

        Assert.Contains(dialogs.LastSettingsTabs!, t => t is McpSettingsTab);
        Assert.Equal((20, 15, 80), (app.Vm.Limits.MaxTools, app.Vm.Limits.ListLimit, app.Vm.Limits.MaxListLimit));
        var saved = app.Get<IUserStateStore>().Load();
        Assert.Equal((20, 15, 80), (saved.McpMaxTools, saved.McpListLimit, saved.McpMaxListLimit));
    }

    [Fact]
    public async Task The_dialog_starts_from_the_saved_limits_and_cancelling_changes_nothing()
    {
        var memory = new MemoryUserStateStore();
        memory.State.McpMaxTools = 18;
        memory.State.McpListLimit = 9;
        memory.State.McpMaxListLimit = 70;
        using var app = TestApp.Create(s => s.AddSingleton<IUserStateStore>(memory));
        var dialogs = app.Get<FakeDialogs>();
        dialogs.SettingsAnswer = _ => null;

        await app.Vm.OpenSettingsCommand.ExecuteAsync(null);

        var tab = dialogs.LastSettingsTabs!.OfType<McpSettingsTab>().Single();
        Assert.Equal((18, 9, 70), (tab.MaxTools, tab.ListLimit, tab.MaxListLimit));
        Assert.Equal((18, 9, 70), (app.Vm.Limits.MaxTools, app.Vm.Limits.ListLimit, app.Vm.Limits.MaxListLimit));
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (Find<T>(child) is { } nested) return nested;
        }
        return null;
    }

    [Fact]
    public void Settings_window_shows_the_mcp_view_with_three_numeric_fields()
    {
        WpfHost.Run(() =>
        {
            var vm = new SettingsViewModel(Current(), [new McpSettingsTab(Current(25, 10, 60))]);
            var window = new SettingsWindow { DataContext = vm, ShowInTaskbar = false };
            window.EnsureTabViews();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var view = Find<McpSettingsTabView>(window);
            Assert.NotNull(view);
            var boxes = new List<System.Windows.Controls.TextBox>();
            void Collect(DependencyObject d)
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                {
                    var c = VisualTreeHelper.GetChild(d, i);
                    if (c is System.Windows.Controls.TextBox box) boxes.Add(box);
                    Collect(c);
                }
            }
            Collect(view!);

            Assert.Equal(["25", "10", "60"], boxes.Select(b => b.Text));
            window.Close();
        });
    }

    [Fact]
    public async Task The_cli_prints_a_warning_on_stderr_when_the_tool_count_exceeds_the_limit()
    {
        var studio = new RecordingStudio();
        var limits = new McpLimits();
        limits.Set(5, 30, 100);
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await Cli.McpCli.RunAsync(["mcp", "tools"], output, error, () => Task.FromResult<IStudioApi>(studio), limits: limits);

        Assert.Equal(0, code);
        Assert.Contains("przekracza limit 5", error.ToString());
        Assert.DoesNotContain("przekracza", output.ToString());
    }
}
