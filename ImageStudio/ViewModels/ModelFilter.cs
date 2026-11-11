using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageStudio.Configuration;

namespace ImageStudio.ViewModels;

// Filtry jednego selektora modeli (szukaj, dostawca, obraz na wejściu, NSFW, preferowane). Każdy selektor ma własny egzemplarz,
// więc ustawienia zakładek nie wpływają na siebie. Panel filtrów (ModelFilterBar) dostaje go jako DataContext.
public sealed partial class ModelFilter(bool imageFilter, Func<ProviderModelPrefs> prefs, ICommand openModelBrowser) : ObservableObject
{
    public const string AllProviders = ModelOption.AllOwners;

    // Zmiana dowolnego filtru: widoki zakładki odświeżają listę
    public event Action? Changed;

    // Zmiana "tylko preferowane": zapis ostatniego wyboru jako domyślnego dostawcy
    public event Action<bool>? PreferredOnlyToggled;

    public ICommand OpenModelBrowserCommand { get; } = openModelBrowser;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _inputImageOnly;

    [ObservableProperty]
    private TriState _nsfw = TriState.Any;

    [ObservableProperty]
    private bool _preferredOnly;

    private string _selectedProvider = AllProviders;

    // ComboBox przy wymianie pozycji listy zgłasza null: to nie jest decyzja użytkownika, więc wybór zostaje
    public string SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (value is null)
            {
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _selectedProvider, value)) Refresh();
        }
    }

    public void Refresh() => Changed?.Invoke();

    // Zmiana dostawcy AI: filtry z poprzedniego ukryłyby wszystkie nowe modele
    public void Reset()
    {
        SearchText = "";
        SelectedProvider = AllProviders;
    }

    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnInputImageOnlyChanged(bool value) => Refresh();
    partial void OnNsfwChanged(TriState value) => Refresh();

    partial void OnPreferredOnlyChanged(bool value)
    {
        PreferredOnlyToggled?.Invoke(value);
        Refresh();
    }

    public bool Matches(object item)
    {
        if (item is not ModelOption m) return false;
        if (Nsfw == TriState.Only && !m.Nsfw) return false;
        if (Nsfw == TriState.Exclude && m.Nsfw) return false;
        // Modele dźwięku nie mają wejścia obrazowego: filtr obrazu ukryłby je wszystkie
        if (imageFilter && InputImageOnly && !m.AcceptsImage) return false;
        if (SelectedProvider != AllProviders && !string.Equals(m.OwnedBy, SelectedProvider, StringComparison.OrdinalIgnoreCase)) return false;
        // Pusta lista preferowanych nie ukrywa wszystkiego: filtr działa dopiero po dodaniu pierwszego modelu
        if (PreferredOnly && prefs() is { Preferred.Count: > 0 } p && !p.Preferred.Contains(m.Id)) return false;
        return string.IsNullOrWhiteSpace(SearchText) || m.Id.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }
}
