using System.IO;
using System.Text.Json;

namespace ImageStudio.Configuration;

// Położenie i rozmiar okna (bez stanu zminimalizowanego); klucz to pełna nazwa typu okna
public sealed record WindowRecord(double Left, double Top, double Width, double Height, bool Maximized);

// Zapamiętane rozmiary okien i szerokości kolumn paneli: %LOCALAPPDATA%\ImageStudio\windows.json.
// Uszkodzony plik lub błąd zapisu nie blokuje aplikacji (okna wracają do rozmiarów z XAML)
public static class WindowStateStore
{
    private sealed class FileModel
    {
        public Dictionary<string, WindowRecord> Windows { get; set; } = [];
        public Dictionary<string, double> Columns { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private static FileModel? _model;

    private static string FilePath => Path.Combine(AppPaths.UserDataDir, "windows.json");

    private static FileModel Model => _model ??= Load();

    private static FileModel Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<FileModel>(File.ReadAllText(FilePath)) ?? new FileModel()
                : new FileModel();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new FileModel();
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.UserDataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Model, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Zapis jest wygodą, nie wymogiem
        }
    }

    public static WindowRecord? GetWindow(string key)
    {
        lock (Gate) return Model.Windows.GetValueOrDefault(key);
    }

    public static void SetWindow(string key, WindowRecord record)
    {
        lock (Gate)
        {
            Model.Windows[key] = record;
            Save();
        }
    }

    public static double? GetColumn(string key)
    {
        lock (Gate) return Model.Columns.TryGetValue(key, out var width) ? width : null;
    }

    public static void SetColumn(string key, double width)
    {
        lock (Gate)
        {
            Model.Columns[key] = width;
            Save();
        }
    }

    // Wymusza ponowne wczytanie z dysku (testy)
    public static void Reload()
    {
        lock (Gate) _model = null;
    }
}
