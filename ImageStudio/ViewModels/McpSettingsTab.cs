using CommunityToolkit.Mvvm.ComponentModel;
using ImageStudio.Localization;
using ImageStudio.Services;

namespace ImageStudio.ViewModels;

// Zakładka "MCP": limity narzędzi. Powyżej MaxTools wystawionych narzędzi aplikacja ostrzega, a narzędzia list_* mają
// domyślną i największą liczbę wyników (ucięty wynik niesie ostrzeżenie). Wartości spoza zakresu są przycinane przy zapisie.
public sealed partial class McpSettingsTab : ObservableObject, ISettingsTab
{
    public string Title => Loc.T("settings.tab.mcp");
    public int Order => 6;

    [ObservableProperty]
    private int _maxTools;

    [ObservableProperty]
    private int _listLimit;

    [ObservableProperty]
    private int _maxListLimit;

    public McpSettingsTab(SettingsResult current)
    {
        _maxTools = current.McpMaxTools;
        _listLimit = current.McpListLimit;
        _maxListLimit = current.McpMaxListLimit;
    }

    public SettingsResult Apply(SettingsResult result)
    {
        var (tools, list, max) = McpLimits.Normalize(MaxTools, ListLimit, MaxListLimit);
        return result with { McpMaxTools = tools, McpListLimit = list, McpMaxListLimit = max };
    }
}
