using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageStudio.Configuration;

namespace ImageStudio.ViewModels;

// Wiersz przeglądarki: model, rodzaje (generowanie / edycja / wideo), opis i flaga "preferowany"
public sealed partial class ModelBrowserItem : ObservableObject
{
    public ModelBrowserItem(ModelOption option, IReadOnlyList<string> kindList, bool preferred)
    {
        Option = option;
        KindList = kindList;
        Kinds = string.Join(", ", kindList);
        _isPreferred = preferred;
    }

    public ModelOption Option { get; }
    public IReadOnlyList<string> KindList { get; }
    public string Kinds { get; }

    // Opis z API; gdy go brak, zbudowany z atrybutów modelu
    public string Description => Option.Description is { Length: > 0 } d
        ? d
        : $"Dostawca: {Option.OwnedBy ?? "—"}. Wejście: {string.Join(", ", Option.InputModalities)}."
          + (Option.Nsfw ? " Model NSFW." : "");

    [ObservableProperty]
    private bool _isPreferred;
}

// Modalna przeglądarka modeli dostawcy: wyszukiwanie, opisy i dodawanie do preferowanych (zapis od razu)
public sealed partial class ModelBrowserViewModel : ObservableObject
{
    private readonly ProviderModelPrefs _prefs;
    private readonly ModelPreferences _store;

    public ModelBrowserViewModel(string providerName, IEnumerable<(ModelOption Option, string Kind)> models,
                                 ProviderModelPrefs prefs, ModelPreferences store)
    {
        ProviderName = providerName;
        _prefs = prefs;
        _store = store;

        foreach (var group in models.GroupBy(m => m.Option.Id).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var item = new ModelBrowserItem(group.First().Option, group.Select(g => g.Kind).Distinct().ToList(),
                                            prefs.Preferred.Contains(group.Key));
            item.PropertyChanged += OnItemChanged;
            Items.Add(item);
        }

        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = Matches;

        foreach (var owner in Items.Select(i => i.Option.OwnedBy).OfType<string>().Distinct()
                     .OrderBy(o => o, StringComparer.OrdinalIgnoreCase))
            Owners.Add(owner);
    }

    public const string All = "Wszystkie";

    public IReadOnlyList<string> Kinds { get; } = [All, "generowanie", "edycja", "wideo", "czat", "mowa", "transkrypcja"];
    public ObservableCollection<string> Owners { get; } = [All];

    private bool Matches(object o) =>
        o is ModelBrowserItem i
        && (!OnlyPreferred || i.IsPreferred)
        && (SelectedKind == All || i.KindList.Contains(SelectedKind))
        && (SelectedOwner == All || i.Option.OwnedBy == SelectedOwner)
        && (!AcceptsImageOnly || i.Option.AcceptsImage)
        && (!HideNsfw || !i.Option.Nsfw)
        && (string.IsNullOrWhiteSpace(SearchText)
            || i.Option.Id.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || i.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    public string ProviderName { get; }
    public ObservableCollection<ModelBrowserItem> Items { get; } = [];
    public ICollectionView View { get; }

    public string Summary => $"Preferowane: {_prefs.Preferred.Count} z {Items.Count}";

    [ObservableProperty]
    private ModelBrowserItem? _selected;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _onlyPreferred;

    [ObservableProperty]
    private string _selectedKind = All;

    [ObservableProperty]
    private string _selectedOwner = All;

    [ObservableProperty]
    private bool _acceptsImageOnly;

    [ObservableProperty]
    private bool _hideNsfw;

    partial void OnSearchTextChanged(string value) => View.Refresh();
    partial void OnOnlyPreferredChanged(bool value) => View.Refresh();
    partial void OnSelectedKindChanged(string value) => View.Refresh();
    partial void OnSelectedOwnerChanged(string value) => View.Refresh();
    partial void OnAcceptsImageOnlyChanged(bool value) => View.Refresh();
    partial void OnHideNsfwChanged(bool value) => View.Refresh();

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ModelBrowserItem item || e.PropertyName != nameof(ModelBrowserItem.IsPreferred)) return;

        if (item.IsPreferred) _prefs.Preferred.Add(item.Option.Id);
        else _prefs.Preferred.Remove(item.Option.Id);
        _store.Save();
        OnPropertyChanged(nameof(Summary));
        if (OnlyPreferred) View.Refresh();
    }
}
