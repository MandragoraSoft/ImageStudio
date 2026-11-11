using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ImageStudio.Tests;

// Zarządzanie narzędziami czatu: wyłączone narzędzia MCP, szacunek tokenów, szukanie, sortowanie i profile
public sealed class ToolManagementTests
{
    private const string Token = "tok-tools";

    // Lokalny serwer MCP z narzędziami aplikacji, połączony przez mcp.json (tak jak w aplikacji)
    private static async Task<(ToolsViewModel Vm, McpAppServer Server, string Dir)> ConnectLocalServerAsync(TestApp app)
    {
        var port = McpAppServerTests.FreePort();
        var server = new McpAppServer(new AppControlTools(app.Vm, _ => true), action => action());
        await server.StartAsync(port, Token);

        var dir = Path.Combine(Path.GetTempPath(), "imagestudio-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = Path.Combine(dir, "mcp.json");
        File.WriteAllText(config, "{\"mcpServers\":{\"lokalny\":{\"url\":\"" + McpAppServer.EndpointFor(port)
                                  + "\",\"headers\":{\"Authorization\":\"Bearer " + Token + "\"}}}}");

        var vm = new ToolsViewModel(new McpToolHost(), useTools: true, requireApproval: false, enabledServers: ["lokalny"], config);
        await vm.InitializeAsync();
        return (vm, server, dir);
    }

    [Fact]
    public async Task Disabled_mcp_tool_is_not_sent()
    {
        using var app = TestApp.Create();
        var (vm, server, dir) = await ConnectLocalServerAsync(app);
        await using var _ = server;
        try
        {
            var tool = vm.ToolGroups.Single(g => g.Title == "lokalny").Tools.First();
            var before = vm.Tools.Count;
            Assert.Contains(tool.Name, vm.Tools.Select(t => t.Name));

            tool.IsEnabled = false;

            Assert.DoesNotContain(tool.Name, vm.Tools.Select(t => t.Name));
            Assert.Equal(before - 1, vm.Tools.Count);
            Assert.Equal($"Dostępne narzędzia: {before - 1}", vm.Summary);
            await vm.DisposeAsync();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Tool_item_shows_estimated_tokens()
    {
        using var app = TestApp.Create();
        var vm = app.Vm.Tools;
        vm.UseTools = true;
        var item = vm.ToolGroups.SelectMany(g => g.Tools).First(t => t.Name == "builtin__get_datetime");
        var definition = vm.Tools.Single(t => t.Name == item.Name);

        Assert.Equal(TokenEstimator.Estimate(definition), item.Tokens);
        Assert.Equal("~" + TokenEstimator.Short(item.Tokens), item.TokensText);
    }

    [Fact]
    public void Group_tokens_count_only_enabled_tools()
    {
        using var app = TestApp.Create();
        var group = app.Vm.Tools.ToolGroups.First(g => g.Tools.Count > 1);
        var tool = group.Tools[0];
        var enabledBefore = group.Tokens;
        var totalBefore = group.TotalTokens;

        tool.IsEnabled = false;

        Assert.Equal(enabledBefore - tool.Tokens, group.Tokens);
        Assert.Equal(totalBefore, group.TotalTokens);
    }

    [Fact]
    public void Tool_tokens_sum_matches_the_tools_sent()
    {
        using var app = TestApp.Create();
        var vm = app.Vm.Tools;
        vm.UseTools = true;

        Assert.Equal(vm.Tools.Sum(t => TokenEstimator.Estimate(t)), vm.ToolTokens);
        Assert.Contains(TokenEstimator.Short(vm.ToolTokens), vm.TokensSummary);
    }

    [Fact]
    public void Tokens_summary_is_empty_when_tools_are_off()
    {
        using var app = TestApp.Create();
        var vm = app.Vm.Tools;
        vm.UseTools = false;

        Assert.Equal("", vm.TokensSummary);
    }

    [Fact]
    public void Search_hides_non_matching_tools_and_their_groups()
    {
        using var app = TestApp.Create();
        var vm = app.Vm.Tools;
        var tools = vm.ToolGroups.SelectMany(g => g.Tools).ToList();

        vm.ToolSearch = "get_datetime";

        Assert.Equal(["builtin__get_datetime"], tools.Where(t => t.IsVisible).Select(t => t.Name));
        var datetimeGroup = vm.ToolGroups.First(g => g.Tools.Any(t => t.Name == "builtin__get_datetime"));
        Assert.True(datetimeGroup.IsVisible);
        Assert.Equal(1, vm.ToolGroups.Count(g => g.IsVisible));

        vm.ToolSearch = "";

        Assert.All(tools, t => Assert.True(t.IsVisible));
        Assert.All(vm.ToolGroups, g => Assert.True(g.IsVisible));
    }

    [Fact]
    public void Search_is_case_insensitive()
    {
        using var app = TestApp.Create();
        var vm = app.Vm.Tools;

        vm.ToolSearch = "GET_DATETIME";

        Assert.True(vm.ToolGroups.SelectMany(g => g.Tools).Single(t => t.Name == "builtin__get_datetime").IsVisible);
    }

    [Fact]
    public void Sort_by_size_orders_groups_descending_and_switching_off_restores_order()
    {
        using var app = TestApp.Create();
        var vm = app.Vm.Tools;
        var defaultOrder = vm.ToolGroups.Select(g => g.Title).ToList();

        vm.SortBySize = true;

        var sizes = vm.ToolGroups.Select(g => g.TotalTokens).ToList();
        Assert.Equal(sizes.OrderByDescending(x => x), sizes);

        vm.SortBySize = false;

        Assert.Equal(defaultOrder, vm.ToolGroups.Select(g => g.Title));
    }

    [Fact]
    public void Save_and_apply_profile_restores_the_disabled_set()
    {
        using var app = TestApp.Create();
        var vm = app.Vm.Tools;
        var tool = vm.ToolGroups.SelectMany(g => g.Tools).First(t => t.Name == "builtin__get_datetime");
        tool.IsEnabled = false;
        vm.ProfileName = "bez czasu";
        vm.SaveProfileCommand.Execute(null);

        tool.IsEnabled = true;
        vm.SelectedProfile = "bez czasu";
        vm.ApplyProfileCommand.Execute(null);

        // Zastosowanie odbudowuje listę grup, więc narzędzie trzeba pobrać od nowa
        var restored = vm.ToolGroups.SelectMany(g => g.Tools).First(t => t.Name == "builtin__get_datetime");
        Assert.False(restored.IsEnabled);
        Assert.Contains("builtin__get_datetime", vm.DisabledTools);
    }

    [Fact]
    public void Saving_the_same_name_overwrites_the_profile()
    {
        using var app = TestApp.Create();
        var vm = app.Vm.Tools;
        var tools = vm.ToolGroups.SelectMany(g => g.Tools).ToList();
        vm.ProfileName = "p";
        tools[0].IsEnabled = false;
        vm.SaveProfileCommand.Execute(null);

        tools[0].IsEnabled = true;
        tools[1].IsEnabled = false;
        vm.SaveProfileCommand.Execute(null);

        Assert.Equal(["p"], vm.ProfileNames);
        Assert.Contains(tools[1].Name, vm.Profiles["p"]);
        Assert.DoesNotContain(tools[0].Name, vm.Profiles["p"]);
    }

    [Fact]
    public void Delete_profile_removes_it()
    {
        using var app = TestApp.Create();
        var vm = app.Vm.Tools;
        vm.ProfileName = "p";
        vm.SaveProfileCommand.Execute(null);

        vm.SelectedProfile = "p";
        vm.DeleteProfileCommand.Execute(null);

        Assert.Empty(vm.ProfileNames);
        Assert.DoesNotContain("p", vm.Profiles.Keys);
    }

    [Fact]
    public void Empty_profile_name_is_not_saved()
    {
        using var app = TestApp.Create();
        var vm = app.Vm.Tools;
        vm.ProfileName = "   ";

        vm.SaveProfileCommand.Execute(null);

        Assert.Empty(vm.Profiles);
        Assert.False(vm.ApplyProfileCommand.CanExecute(null));
    }

    [Fact]
    public void Profile_ignores_unknown_names_and_keeps_new_tools_enabled()
    {
        var builtin = BuiltinToolRegistry.Create(new MemoryMemoryStore(), Path.Combine(Path.GetTempPath(), "ws-" + Guid.NewGuid().ToString("N")), _ => true);
        var profiles = new Dictionary<string, List<string>> { ["stary"] = ["nie__istnieje", "builtin__get_datetime"] };
        var vm = new ToolsViewModel(new McpToolHost(), true, true, [], null, null, false, null, builtin, true, profiles: profiles);

        vm.SelectedProfile = "stary";
        vm.ApplyProfileCommand.Execute(null);

        var tools = vm.ToolGroups.SelectMany(g => g.Tools).ToList();
        Assert.False(tools.Single(t => t.Name == "builtin__get_datetime").IsEnabled);
        Assert.True(tools.Single(t => t.Name == "builtin__memory_save").IsEnabled);
    }

    [Fact]
    public async Task Profiles_are_saved_in_state_and_loaded_again()
    {
        var store = new MemoryUserStateStore();
        using (var app = TestApp.Create(s => s.AddSingleton<IUserStateStore>(store)))
        {
            await app.Vm.InitializeAsync(); // zapis stanu rusza dopiero po starcie (MainViewModel._ready)
            app.Vm.Tools.ProfileName = "zapisany";
            app.Vm.Tools.SaveProfileCommand.Execute(null);
        }

        Assert.Equal(["zapisany"], store.State.ToolProfiles.Keys);

        using var again = TestApp.Create(s => s.AddSingleton<IUserStateStore>(store));
        Assert.Equal(["zapisany"], again.Vm.Tools.ProfileNames.ToList());
    }

    [Fact]
    public void Context_breakdown_lists_four_parts()
    {
        using var app = TestApp.Create();
        var chat = app.Vm.Chat;
        Assert.Equal(4, chat.ContextBreakdown.Split('\n').Length);

        chat.Messages.Add(new ChatMessage("tool", new string('x', 40)));

        Assert.Contains("~10", chat.ContextBreakdown.Split('\n')[3]);
    }
}
