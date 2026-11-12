using System.IO;
using System.Text.Json;

namespace ImageStudio.Configuration;

// Galeria zakładki Głos: wyniki mowy (Kind "speech", Prompt = przeczytany tekst) i transkrypcje (Kind "transcript", Prompt = tekst z nagrania).
// FilePath to plik dźwiękowy (może go nie być: tekst zostaje), Size to dopisek (np. język tłumaczenia)
public interface IVoiceGalleryStore
{
    IReadOnlyList<GalleryEntry> Load();
    void Save(IEnumerable<GalleryEntry> entries);
}

// voice.json w katalogu danych użytkownika; same pliki dźwiękowe leżą w Muzyka\ImageStudio
public sealed class FileVoiceGalleryStore : IVoiceGalleryStore
{
    public const int MaxEntries = 200;

    private static string FilePath => Path.Combine(AppPaths.UserDataDir, "voice.json");

    public IReadOnlyList<GalleryEntry> Load()
    {
        if (!File.Exists(FilePath)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<GalleryEntry>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (JsonException)
        {
            return [];  // uszkodzony plik nie blokuje startu
        }
    }

    public void Save(IEnumerable<GalleryEntry> entries)
    {
        var snapshot = entries.Take(MaxEntries).ToList();
        UserDataMutationGate.TryWrite(AppPaths.UserDataDir, FilePath, () =>
        {
            Directory.CreateDirectory(AppPaths.UserDataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot,
                                                                 new JsonSerializerOptions { WriteIndented = true }));
        });
    }
}
