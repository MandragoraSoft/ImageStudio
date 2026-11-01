using System.IO;
using System.Text;
using System.Text.Json;
using static ImageStudio.Services.ToolArgs;

namespace ImageStudio.Services;

// Grupa "files": pliki tekstowe w jednym folderze roboczym (files_list, files_search, files_read, files_write).
// Ścieżki bezwzględne, "..", dyski, strumienie NTFS i dowiązania (punkty reparse) są odrzucane; zapis wymaga zgody (Writes)
public sealed class FileTools : IChatToolGroup
{
    private const int MaxWriteChars = 1_000_000;
    private const int DefaultReadChars = 20_000;
    private const int MaxReadChars = 100_000;
    private const int MaxListEntries = 200;
    private const int MaxSearchResults = 50;
    private const int MaxSearchFiles = 5_000;
    private const long MaxSearchFileBytes = 2_000_000;
    private const int MaxSearchLineChars = 200;

    // Pliki, które można by uruchomić jednym kliknięciem: model ich nie zapisuje
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".com", ".scr", ".msi", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".hta",
        ".lnk", ".reg", ".jar", ".cpl", ".url",
    };

    private readonly string _root;

    public FileTools(string workspaceRoot)
    {
        _root = Path.GetFullPath(workspaceRoot);
    }

    public string Id => "files";
    public string WorkspaceRoot => _root;

    public IReadOnlyList<ChatToolInfo> Tools { get; } =
    [
        new("files_list", "Lista plików i folderów w folderze roboczym (ścieżka względna; pusta = główny folder).",
            Schema(("path", """{"type":"string","description":"Podfolder względem folderu roboczego"}""", false))),
        new("files_search",
            "Szuka w folderze roboczym: po fragmencie nazwy pliku i w treści plików tekstowych (bez rozróżniania wielkości liter). Zwraca ścieżki, a dla treści numer linii i jej początek.",
            Schema(("query", """{"type":"string","description":"Szukany tekst (w nazwie pliku i w treści)"}""", false),
                   ("name_pattern", """{"type":"string","description":"Ogranicza do plików o nazwie pasującej do wzorca, np. *.md albo notatki-?.txt"}""", false),
                   ("path", """{"type":"string","description":"Podfolder, od którego szukać (domyślnie cały folder roboczy)"}""", false))),
        new("files_read", "Czyta plik tekstowy z folderu roboczego (UTF-8).",
            Schema(("path", """{"type":"string","description":"Ścieżka pliku względem folderu roboczego"}""", true),
                   ("max_chars", $$"""{"type":"integer","minimum":1,"maximum":{{MaxReadChars}},"description":"Ile znaków wczytać (domyślnie {{DefaultReadChars}})"}""", false))),
        new("files_write",
            "Zapisuje plik tekstowy w folderze roboczym (wymaga zgody użytkownika). Tryby: create (domyślnie, błąd gdy plik istnieje), overwrite, append.",
            Schema(("path", """{"type":"string","description":"Ścieżka pliku względem folderu roboczego"}""", true),
                   ("content", """{"type":"string","description":"Treść pliku"}""", true),
                   ("mode", """{"type":"string","enum":["create","overwrite","append"],"description":"Tryb zapisu"}""", false)),
            Writes: true),
    ];

    public string Call(string name, JsonElement arguments) => name switch
    {
        "files_list" => ListFiles(arguments),
        "files_search" => SearchFiles(arguments),
        "files_read" => ReadFile(arguments),
        "files_write" => WriteFile(arguments),
        _ => throw new ArgumentException($"Nieznane narzędzie: {name}"),
    };

    private string ListFiles(JsonElement args)
    {
        Directory.CreateDirectory(_root);
        var directory = Resolve(OptString(args, "path") ?? "");
        if (!Directory.Exists(directory)) throw new ArgumentException("Nie ma takiego folderu.");

        var entries = new DirectoryInfo(directory).EnumerateFileSystemInfos()
            .Where(e => !e.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .OrderBy(e => e is not DirectoryInfo).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxListEntries)
            .Select(e => e is FileInfo f ? $"{f.Name} ({f.Length} B)" : e.Name + "/")
            .ToList();
        return entries.Count == 0 ? "Folder jest pusty." : string.Join("\n", entries);
    }

    // Szukanie po nazwie i treści; folder jest przeglądany bez dowiązań, pliki binarne i duże są pomijane, wyniki i liczba plików ograniczone
    private string SearchFiles(JsonElement args)
    {
        var query = (OptString(args, "query") ?? "").Trim();
        var pattern = OptString(args, "name_pattern");
        if (query.Length == 0 && string.IsNullOrWhiteSpace(pattern)) throw new ArgumentException("Podaj query albo name_pattern.");

        Directory.CreateDirectory(_root);
        var start = Resolve(OptString(args, "path") ?? "");
        if (!Directory.Exists(start)) throw new ArgumentException("Nie ma takiego folderu.");

        var nameFilter = string.IsNullOrWhiteSpace(pattern) ? null : GlobToRegex(pattern);
        var results = new List<string>();
        var scanned = 0;
        var truncated = false;

        foreach (var file in EnumerateFiles(start))
        {
            if (++scanned > MaxSearchFiles || results.Count >= MaxSearchResults)
            {
                truncated = true;
                break;
            }

            if (nameFilter is not null && !nameFilter.IsMatch(file.Name)) continue;

            var relative = Path.GetRelativePath(_root, file.FullName);
            if (query.Length == 0)
            {
                results.Add(relative);
                continue;
            }

            if (file.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) results.Add($"{relative} (nazwa)");
            if (file.Length > MaxSearchFileBytes) continue;

            try
            {
                var number = 0;
                foreach (var line in File.ReadLines(file.FullName))
                {
                    number++;
                    if (line.Contains('\0')) break;  // plik binarny
                    if (!line.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                    var text = line.Trim();
                    results.Add($"{relative}:{number}: {(text.Length > MaxSearchLineChars ? text[..MaxSearchLineChars] + "…" : text)}");
                    if (results.Count >= MaxSearchResults)
                    {
                        truncated = true;
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                // nieczytelny plik jest pomijany
            }
        }

        if (results.Count == 0) return "Nic nie znaleziono.";
        return string.Join("\n", results) + (truncated ? "\n…[wyniki obcięte: zawęź zapytanie]" : "");
    }

    // Pliki od podanego folderu w głąb, alfabetycznie, bez folderów i plików będących dowiązaniami
    private static IEnumerable<FileInfo> EnumerateFiles(string start)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(start));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = directory.EnumerateFileSystemInfos().Where(e => !e.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in entries.OfType<FileInfo>()) yield return file;
            foreach (var sub in entries.OfType<DirectoryInfo>().Reverse()) pending.Push(sub);
        }
    }

    // Wzorzec nazwy z * i ? zamieniony na wyrażenie regularne (reszta znaków dosłownie)
    private static System.Text.RegularExpressions.Regex GlobToRegex(string pattern) =>
        new("^" + System.Text.RegularExpressions.Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private string ReadFile(JsonElement args)
    {
        var path = Resolve(RequiredString(args, "path"));
        if (!File.Exists(path)) throw new ArgumentException("Nie ma takiego pliku.");

        var limit = Math.Clamp(OptInt(args, "max_chars") ?? DefaultReadChars, 1, MaxReadChars);
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[limit + 1];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        if (Array.IndexOf(buffer, '\0', 0, read) >= 0) throw new ArgumentException("To plik binarny, nie tekstowy.");

        return read > limit ? new string(buffer, 0, limit) + "\n…[obcięto]" : new string(buffer, 0, read);
    }

    private string WriteFile(JsonElement args)
    {
        var path = Resolve(RequiredString(args, "path"));
        var content = RequiredString(args, "content");
        var mode = OptString(args, "mode") ?? "create";
        if (path == _root || Directory.Exists(path)) throw new ArgumentException("Ścieżka wskazuje folder, nie plik.");
        if (BlockedExtensions.Contains(Path.GetExtension(path))) throw new ArgumentException("Tego typu pliku (wykonywalny lub skrypt) nie wolno zapisywać.");
        if (content.Length > MaxWriteChars) throw new ArgumentException($"Za duża treść (maksymalnie {MaxWriteChars} znaków).");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        switch (mode)
        {
            case "create":
                if (File.Exists(path)) throw new ArgumentException("Plik już istnieje: użyj trybu overwrite albo append.");
                File.WriteAllText(path, content, new UTF8Encoding(false));
                break;
            case "overwrite":
                File.WriteAllText(path, content, new UTF8Encoding(false));
                break;
            case "append":
                File.AppendAllText(path, content, new UTF8Encoding(false));
                break;
            default:
                throw new ArgumentException("Nieznany tryb zapisu (create, overwrite, append).");
        }

        return $"Zapisano {Path.GetRelativePath(_root, path)} ({content.Length} znaków).";
    }

    // Ścieżka względna wewnątrz folderu roboczego; bezwzględne, "..", dyski i strumienie NTFS (":") oraz dowiązania są odrzucane
    private string Resolve(string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new ArgumentException("Ścieżka musi być względna (wewnątrz folderu roboczego).");

        var full = Path.GetFullPath(Path.Combine(_root, relative));
        var rootWithSeparator = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!(full + Path.DirectorySeparatorChar).StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Ścieżka wychodzi poza folder roboczy.");

        var current = _root;
        foreach (var part in Path.GetRelativePath(_root, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;

            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && new FileInfo(current).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("Dowiązania w folderze roboczym są niedozwolone.");
        }

        return full;
    }
}
