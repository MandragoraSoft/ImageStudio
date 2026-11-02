using System.IO;

namespace ImageStudio.Configuration;

// Wczytuje plik .env (KEY=VALUE, # komentarz, wartość w cudzysłowie) do zmiennych środowiska procesu.
// Zmienna już ustawiona w powłoce ma pierwszeństwo, a pierwszy znaleziony plik przed następnymi, więc wartości z
// ustawień aplikacji i sekretów dalej wygrywają z tym, co ustawiono wyżej w kolejności odczytu.
public static class DotEnv
{
    // %LOCALAPPDATA%\ImageStudio\.env zawsze; w Debug także .env szukany od katalogu aplikacji i bieżącego w górę
    // (repozytorium podczas pracy). W wydaniu nie przeszukujemy katalogów nadrzędnych: obcy .env nie ma wpływu.
    public static void LoadDefaults()
    {
        List<string> files = [];
#if DEBUG
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, ".env"))) files.Add(Path.Combine(dir.FullName, ".env"));
#endif
        files.Add(Path.Combine(AppPaths.UserDataDir, ".env"));
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists)) Load(file);
    }

    public static void Load(string path)
    {
        foreach (var (key, value) in Parse(File.ReadAllLines(path)))
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))) Environment.SetEnvironmentVariable(key, value);
    }

    public static IEnumerable<(string Key, string Value)> Parse(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0]) value = value[1..^1];
            yield return (line[..eq].Trim(), value);
        }
    }
}
