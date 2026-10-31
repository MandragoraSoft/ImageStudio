namespace ImageStudio.ViewModels;

// Referencja: ścieżka pliku do wysłania, krótka etykieta do listy i pełne źródło (plik lub URL) do podpowiedzi
public sealed record ReferenceItem(string Path, string Label, string Source)
{
    private const int MaxLabel = 60;

    // Długi link na liście kończy się "..."; pełny adres jest w podpowiedzi
    public static string Shorten(string text) =>
        text.Length <= MaxLabel ? text : text[..(MaxLabel - 3)] + "...";
}
