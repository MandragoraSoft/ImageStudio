using System.ComponentModel;

namespace ImageStudio.Localization;

// Źródło danych dla bindowania w XAML: indeksator zwraca tekst klucza, a zmiana języka odświeża wszystkie powiązania naraz
public sealed class LocalizationSource : INotifyPropertyChanged
{
    public static LocalizationSource Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Loc.T(key);

    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}
