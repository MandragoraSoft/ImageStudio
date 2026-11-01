using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ImageStudio.ViewModels;

namespace ImageStudio.Views;

public partial class ModelFilterBar : UserControl
{
    public static readonly DependencyProperty ShowImageFilterProperty =
        DependencyProperty.Register(nameof(ShowImageFilter), typeof(bool), typeof(ModelFilterBar), new PropertyMetadata(true));
    public static readonly DependencyProperty ModelsSourceProperty =
        DependencyProperty.Register(nameof(ModelsSource), typeof(IEnumerable), typeof(ModelFilterBar),
            new PropertyMetadata(null, (d, e) => ((ModelFilterBar)d).Attach((IEnumerable?)e.OldValue, (IEnumerable?)e.NewValue)));
    public static readonly DependencyProperty ActiveProviderProperty =
        DependencyProperty.Register(nameof(ActiveProvider), typeof(string), typeof(ModelFilterBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                (d, e) =>
                {
                    var bar = (ModelFilterBar)d;
                    bar._active = (string?)e.NewValue;
                    bar.RebuildProviders();
                },
                // ComboBox zgłasza null, gdy zmiana listy usunie wybraną pozycję: to nie jest decyzja użytkownika
                (d, value) => value ?? ((ModelFilterBar)d)._active));

    // "Przyjmuje obraz" ma sens dla modeli obrazów, edycji, wideo i czatu, nie dla modeli dźwięku
    public bool ShowImageFilter { get => (bool)GetValue(ShowImageFilterProperty); set => SetValue(ShowImageFilterProperty, value); }

    // Modele, które filtruje selektor (widok albo kolekcja): z nich powstaje lista dostawców, więc zakładka
    // nie pokazuje dostawców bez żadnego modelu na swojej liście
    public IEnumerable? ModelsSource { get => (IEnumerable?)GetValue(ModelsSourceProperty); set => SetValue(ModelsSourceProperty, value); }

    private string? _active;

    // Wybrany dostawca (filtr jest wspólny dla zakładek): zostaje na liście, żeby pole nie robiło się puste.
    // Pole listy jest związane z tą właściwością, a ona z filtrem modeli (SelectedProvider)
    public string? ActiveProvider { get => (string?)GetValue(ActiveProviderProperty); set => SetValue(ActiveProviderProperty, value); }

    public ObservableCollection<string> Providers { get; } = [ModelOption.AllOwners];

    public ModelFilterBar()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach(null, ModelsSource);
        Unloaded += (_, _) => Detach(ModelsSource);
    }

    // Źródło bez widoku (kolekcja) albo źródło widoku (ICollectionView filtruje, a lista dostawców ma obejmować wszystko)
    private static IEnumerable? Unfiltered(IEnumerable? source) => source is ICollectionView view ? view.SourceCollection : source;

    private void Attach(IEnumerable? old, IEnumerable? current)
    {
        Detach(old);
        if (Unfiltered(current) is INotifyCollectionChanged changing) changing.CollectionChanged += OnSourceChanged;
        RebuildProviders();
    }

    private void Detach(IEnumerable? source)
    {
        if (Unfiltered(source) is INotifyCollectionChanged changing) changing.CollectionChanged -= OnSourceChanged;
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildProviders();

    // Lista jest uzupełniana różnicowo, bez czyszczenia: wyczyszczenie ComboBoxa zerowałoby wybór dostawcy
    private void RebuildProviders()
    {
        var wanted = (Unfiltered(ModelsSource) ?? Array.Empty<object>()).OfType<ModelOption>()
            .Select(m => m.OwnedBy).Append(ActiveProvider)
            .OfType<string>().Where(o => o.Length > 0 && o != ModelOption.AllOwners)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(o => o, StringComparer.OrdinalIgnoreCase)
            .Prepend(ModelOption.AllOwners).ToList();

        for (var i = Providers.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Providers[i])) Providers.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
            if (i >= Providers.Count || Providers[i] != wanted[i]) Providers.Insert(i, wanted[i]);
    }
}
