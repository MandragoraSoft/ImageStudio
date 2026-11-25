namespace ImageStudio.Storage;

public enum MediaKind { Image, Video, Audio }

// Operacje wejścia/wyjścia magazynu wyników (obrazy, wideo, dźwięk). "Lokalizacja" to nieprzezroczysty tekst wybrany
// przez wtyczkę (dla dysku ścieżka, dla chmury np. identyfikator pliku); aplikacja tylko go przechowuje
// (GalleryEntry.FilePath) i oddaje wtyczce.
public interface IMediaStorage
{
    // Zapisuje plik i zwraca jego lokalizację; fileName to propozycja aplikacji (bez katalogu), wtyczka może ją zmienić
    Task<string> WriteAsync(MediaKind kind, string fileName, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    Task<byte[]> ReadAsync(string location, CancellationToken ct = default);
    Task<bool> ExistsAsync(string location, CancellationToken ct = default);

    // Brak pliku nie jest błędem
    Task DeleteAsync(string location, CancellationToken ct = default);

    // Ścieżka lokalna do odtwarzania, miniatur i kopiowania; wtyczka zdalna pobiera plik do pamięci podręcznej
    Task<string> GetLocalPathAsync(string location, CancellationToken ct = default);
}

// Rodzaj pola w zakładce Zapis: Text i Email to zwykłe pole tekstowe, Folder dodaje przycisk wyboru folderu,
// Password ma ukryte znaki, Code to krótki kod jednorazowy (np. 2FA)
public enum StorageFieldKind { Text, Folder, Email, Password, Code }

// Jedno pole konfiguracji wtyczki. LabelKey to klucz tłumaczenia (brak klucza = wyświetlony tekst klucza);
// IsFolder dodaje do pola przycisk wyboru folderu; IsSecret: wartość trafia do IStorageSecrets, nigdy do pliku
// ustawień (wtyczka zapisuje ją w Configure); Masked: pole z ukrytymi znakami (hasło), gdy fałsz, pole jest jawne
// (np. Client ID, który chcesz zobaczyć, ale nie trzymać w zwykłym pliku)
// Note: krótka informacja pod polem (np. skąd pochodzi wartość)
// Kind: rodzaj pola (null = wynika ze starych flag IsFolder/Masked); Transient: wartość tylko dla akcji (np. hasło do
// logowania): nie trafia do Configure, do pliku ustawień ani do sekretów, okno czyści ją po udanej akcji
public sealed record StorageSetting(string Key, string LabelKey, string Value, bool IsFolder = false, bool IsSecret = false,
                                    bool Masked = false, StorageStatus? Note = null,
                                    StorageFieldKind? Kind = null, bool Transient = false)
{
    public StorageFieldKind EffectiveKind => Kind ?? (IsFolder ? StorageFieldKind.Folder : Masked ? StorageFieldKind.Password : StorageFieldKind.Text);
}

// Przycisk w zakładce wtyczki (np. "Zaloguj"); wywołanie to RunActionAsync(Key); WorkingKey: klucz tekstu pokazywanego
// w trakcie akcji (null = storage.status.working)
public sealed record StorageAction(string Key, string LabelKey, string? WorkingKey = null);

// Prośba wtyczki o dodatkową wartość w trakcie akcji (np. kod 2FA); Message: opis nad polem
public sealed record StoragePrompt(string Key, string LabelKey, StorageFieldKind Kind = StorageFieldKind.Code, StorageStatus? Message = null);

// Rozmowa z użytkownikiem w trakcie akcji (zakładka Zapis pokazuje pole i przycisk Potwierdź)
public interface IStorageInteraction
{
    // Wartość wpisana przez użytkownika; anulowanie (ct albo przycisk Anuluj) kończy się OperationCanceledException
    Task<string?> AskAsync(StoragePrompt prompt, CancellationToken ct);
}

// Brak rozmówcy: akcja wołana bez okna (stara ścieżka RunActionAsync(key))
public static class StorageInteraction
{
    public static IStorageInteraction None { get; } = new NoInteraction();

    private sealed class NoInteraction : IStorageInteraction
    {
        public Task<string?> AskAsync(StoragePrompt prompt, CancellationToken ct) =>
            throw new InvalidOperationException("Ta operacja wymaga danych od użytkownika (Ustawienia, zakładka Zapis).");
    }
}

// Stan do pokazania pod polami: klucz tłumaczenia z opcjonalnym parametrem {0}
public sealed record StorageStatus(string Key, string? Detail = null);

// Sekrety wtyczek (tokeny, hasła) w magazynie aplikacji szyfrowanym DPAPI; wtyczka nie zna pliku ani formatu
public interface IStorageSecrets
{
    string? Load(string name);

    // Pusta wartość usuwa sekret
    void Save(string value, string name);
}

// Wtyczka magazynu w osobnej bibliotece ImageStudio.Storage.* (jak dostawcy w ImageStudio.Providers.*): oprócz operacji
// opisuje swoje ustawienia, które okno Ustawień pokazuje w zakładce "Zapis"
public interface IMediaStoragePlugin : IMediaStorage
{
    string Id { get; }
    string DisplayName { get; }

    // Pola z bieżącymi wartościami
    IReadOnlyList<StorageSetting> Settings { get; }

    // Wartości po kluczach z Settings; pusta wartość = domyślna wtyczki
    void Configure(IReadOnlyDictionary<string, string> values);

    // Folder na dysku, w którym wtyczka trzyma pliki danego rodzaju (null = brak)
    string? LocalFolder(MediaKind kind) => null;

    // Przyciski (np. logowanie), ich obsługa (poprzedzona Configure z bieżącymi wartościami pól), stan i opis pod polami
    IReadOnlyList<StorageAction> Actions => [];
    Task RunActionAsync(string key, CancellationToken ct = default) => Task.CompletedTask;

    // Akcja z wartościami pól nietrwałych (Transient) i możliwością zapytania użytkownika; domyślnie stara ścieżka
    Task RunActionAsync(string key, IReadOnlyDictionary<string, string> inputs, IStorageInteraction interaction,
                        CancellationToken ct = default) => RunActionAsync(key, ct);

    // Czy przycisk jest teraz dostępny (np. "Zaloguj" tylko dla niezalogowanego)
    bool CanRun(string key) => true;
    StorageStatus? Status => null;
    string? HelpKey => null;
}
