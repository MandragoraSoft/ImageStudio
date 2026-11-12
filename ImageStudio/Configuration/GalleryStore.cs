using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace ImageStudio.Configuration;

// Wpis galerii (bufora wyników): prompt, model, rozmiar, koszt i plik obrazu
public sealed class GalleryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Prompt { get; set; } = "";
    public string Model { get; set; } = "";

    // Dostawca AI, który wykonał operację (nazwa wyświetlana); null w starszych wpisach
    public string? Provider { get; set; }
    public string Kind { get; set; } = "generate";
    public string Size { get; set; } = "";

    // Koszt z odpowiedzi API; gdy API go nie zwróciło, szacunek z cennika (CostEstimated)
    public decimal? CostUsd { get; set; }
    public bool CostEstimated { get; set; }
    public string FilePath { get; set; } = "";
    // Magazyn, który zapisał wynik; brak w starszych wpisach oznacza nieznane pochodzenie
    public string? StorageId { get; set; }

    // Wejścia operacji (referencje, załączniki wideo: ścieżki albo adresy) i parametry potrzebne do jej powtórzenia
    // (width, height, seed, negative, quality, duration, aspect, voice, speed, format, language, hint); null w starszych wpisach
    public List<string>? Inputs { get; set; }
    public Dictionary<string, string>? Params { get; set; }

    [JsonIgnore]
    public BitmapSource? Thumbnail { get; set; }

    // Wideo bez miniatury (brak kodeka) dostaje w widoku ikonę, a każde wideo znaczek odtwarzania
    [JsonIgnore]
    public bool IsVideo => Kind == "video";

    [JsonIgnore]
    public string KindLabel => Kind switch
    {
        "edit" => "Edycja",
        "video" => "Wideo",
        _ => "Generowanie",
    };

    [JsonIgnore]
    public string CostLabel => CostUsd is null
        ? "koszt ?"
        : $"{(CostEstimated ? "~" : "")}${CostUsd:0.0000}";
}

// Galeria w %LOCALAPPDATA%\ImageStudio\gallery.json; same pliki obrazów zostają w folderze zapisów
public static class GalleryStore
{
    public const int MaxEntries = 200;

    private static string FilePath => Path.Combine(AppPaths.UserDataDir, "gallery.json");

    // Wpisy, których plik zniknął, są pomijane
    public static List<GalleryEntry> Load()
    {
        if (!File.Exists(FilePath)) return [];

        try
        {
            var entries = JsonSerializer.Deserialize<List<GalleryEntry>>(File.ReadAllText(FilePath)) ?? [];
            foreach (var entry in entries.Where(e => File.Exists(e.FilePath)))
                entry.Thumbnail = Thumbnail(entry.FilePath);
            return entries.Where(e => File.Exists(e.FilePath)).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static void Save(IEnumerable<GalleryEntry> entries)
    {
        Directory.CreateDirectory(AppPaths.UserDataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(entries.Take(MaxEntries).ToList(),
                                                             new JsonSerializerOptions { WriteIndented = true }));
    }

    // Miniatura dekodowana w małym rozmiarze, żeby galeria nie trzymała pełnych obrazów w pamięci
    public static BitmapSource? Thumbnail(string path)
    {
        if (!File.Exists(path)) return null;

        // Wideo: klatka z powłoki Windows (WIC nie czyta mp4); uszkodzone pliki nie są obrazami, wtedy miniatura zostaje pusta
        if (VideoThumbnail.IsVideoFile(path)) return VideoThumbnail.TryGet(path);

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 120;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
