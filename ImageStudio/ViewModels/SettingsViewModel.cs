using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageStudio.ViewModels;

// Okno ustawień: kontener wstrzykniętych zakładek (ISettingsTab). Wynik składa się z wartości wszystkich zakładek
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsResult _current;

    public IReadOnlyList<ISettingsTab> Tabs { get; }

    [ObservableProperty]
    private ISettingsTab _selectedTab;

    public SettingsViewModel(SettingsResult current, IEnumerable<ISettingsTab> tabs)
    {
        _current = current;
        Tabs = tabs.OrderBy(t => t.Order).ToList();
        if (Tabs.Count == 0) throw new ArgumentException("Okno ustawień wymaga co najmniej jednej zakładki.", nameof(tabs));
        _selectedTab = Tabs[0];
    }

    // Bieżący stan wszystkich zakładek jako wynik (przy zapisie okna)
    public SettingsResult ToResult() => Tabs.Aggregate(_current, (result, tab) => tab.Apply(result));
}
