namespace ImageStudio.Configuration;

// Skill: nazwana instrukcja do konkretnego rodzaju zadań (jak skile Claude'a). Model widzi tylko nazwę i opis (w instrukcji systemowej),
// a pełną treść ładuje narzędziem skill_load, gdy zadanie pasuje do opisu
public sealed record Skill(string Name, string Description, string Instructions);

// Magazyn skili: pliki skills\<nazwa>\SKILL.md w katalogu danych użytkownika (użytkownik może je edytować ręcznie)
public interface ISkillStore
{
    IReadOnlyList<Skill> List();
    Skill? Get(string name);
    void Save(Skill skill);

    // false, gdy skila nie było
    bool Delete(string name);

    // Folder do otwarcia z panelu; null = brak (magazyn w pamięci)
    string? FolderPath => null;
}
