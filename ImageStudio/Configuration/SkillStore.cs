using System.IO;
using System.Text;

namespace ImageStudio.Configuration;

// Skile w plikach skills\<nazwa>\SKILL.md (kontrakt ISkillStore: ImageStudio.Tools.Core). Plik ma nagłówek z name i description
// między liniami "---", a dalej treść instrukcji; można go edytować ręcznie. Nazwę sprawdza SkillTools, ale tu też, bo to nazwa folderu
public sealed class FileSkillStore(string? root = null) : ISkillStore
{
    private const string FileName = "SKILL.md";

    private readonly string _root = root ?? Path.Combine(AppPaths.UserDataDir, "skills");

    public string? FolderPath => _root;

    private static bool ValidName(string name) =>
        name.Length is > 0 and <= 40 && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') && name[0] != '-';

    private string PathFor(string name) => Path.Combine(_root, name, FileName);

    public IReadOnlyList<Skill> List()
    {
        if (!Directory.Exists(_root)) return [];

        return Directory.EnumerateDirectories(_root)
            .Select(d => Path.GetFileName(d))
            .Where(ValidName)
            .Order(StringComparer.Ordinal)
            .Select(Get)
            .OfType<Skill>()
            .ToList();
    }

    public Skill? Get(string name)
    {
        if (!ValidName(name) || !File.Exists(PathFor(name))) return null;

        try
        {
            return Parse(name, File.ReadAllText(PathFor(name)));
        }
        catch (IOException)
        {
            return null;  // plik zajęty albo nie do odczytu nie blokuje czatu
        }
    }

    public void Save(Skill skill)
    {
        if (!ValidName(skill.Name)) throw new ArgumentException("Nieprawidłowa nazwa skila.");

        Directory.CreateDirectory(Path.Combine(_root, skill.Name));
        var description = string.Join(' ', skill.Description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        File.WriteAllText(PathFor(skill.Name), $"---\nname: {skill.Name}\ndescription: {description}\n---\n\n{skill.Instructions.Trim()}\n", new UTF8Encoding(false));
    }

    // Usuwa SKILL.md, a folder tylko gdy nic więcej w nim nie ma (użytkownik mógł dołożyć własne pliki)
    public bool Delete(string name)
    {
        if (!ValidName(name) || !File.Exists(PathFor(name))) return false;

        File.Delete(PathFor(name));
        var folder = Path.Combine(_root, name);
        if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        return true;
    }

    // Nagłówek "---\nklucz: wartość\n---"; bez nagłówka cały plik to treść, a opis pusty
    internal static Skill Parse(string name, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var description = "";
        var bodyStart = 0;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            var end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
            if (end > 0)
            {
                foreach (var line in lines[1..end])
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0 && line[..colon].Trim() == "description") description = line[(colon + 1)..].Trim().Trim('"', '\'');
                }

                bodyStart = end + 1;
            }
        }

        return new Skill(name, description, string.Join("\n", lines[bodyStart..]).Trim());
    }
}
