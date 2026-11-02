using System.IO;
using ImageStudio.Configuration;
using ImageStudio.Services;

namespace ImageStudio.Tests;

internal sealed class MemorySkillStore : ISkillStore
{
    private readonly Dictionary<string, Skill> _skills = [];

    public IReadOnlyList<Skill> List() => _skills.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    public Skill? Get(string name) => _skills.GetValueOrDefault(name);
    public void Save(Skill skill) => _skills[skill.Name] = skill;
    public bool Delete(string name) => _skills.Remove(name);
}

// Mechanizm skili: grupa narzędzi (tworzenie, edycja, ładowanie), magazyn plikowy i wpięcie w czat
public sealed class SkillToolsTests
{
    private readonly MemorySkillStore _store = new();
    private SkillTools Tools => new(_store);

    private string Call(string tool, string json) => Tools.Call(tool, System.Text.Json.JsonDocument.Parse(json).RootElement);

    private static string Save(string name = "opis-produktu", string description = "Pisanie opisów produktów", string text = "Pisz krótko.") =>
        System.Text.Json.JsonSerializer.Serialize(new { name, description, instructions = text });

    [Fact]
    public void Save_creates_then_updates_and_load_returns_the_full_text()
    {
        Assert.Equal("Utworzono skill opis-produktu.", Call("skill_save", Save()));
        Assert.Equal("Zaktualizowano skill opis-produktu.", Call("skill_save", Save(text: "Pisz bardzo krótko.")));

        var loaded = Call("skill_load", """{"name":"opis-produktu"}""");
        Assert.Contains("Pisanie opisów produktów", loaded);
        Assert.Contains("Pisz bardzo krótko.", loaded);
        Assert.DoesNotContain("Pisz krótko.", loaded);
        Assert.Single(_store.List());
    }

    [Fact]
    public void List_and_delete_work_and_missing_skills_are_reported_as_text()
    {
        Assert.Equal("Brak skili.", Call("skill_list", "{}"));
        Call("skill_save", Save());
        Assert.Contains("opis-produktu", Call("skill_list", "{}"));
        Assert.Contains("Nie ma skila", Call("skill_load", """{"name":"nie-ma"}"""));

        Assert.Equal("Usunięto skill opis-produktu.", Call("skill_delete", """{"name":"opis-produktu"}"""));
        Assert.Contains("Nie ma skila", Call("skill_delete", """{"name":"opis-produktu"}"""));
    }

    [Theory]
    [InlineData("../zly")]
    [InlineData("Duze")]
    [InlineData("ze spacja")]
    [InlineData("-start")]
    [InlineData("")]
    public void Invalid_names_are_rejected_before_touching_the_store(string name)
    {
        Assert.Throws<ArgumentException>(() => Call("skill_save", Save(name: name)));
        Assert.Throws<ArgumentException>(() => Call("skill_load", System.Text.Json.JsonSerializer.Serialize(new { name })));
        Assert.Empty(_store.List());
    }

    [Fact]
    public void Limits_on_description_text_and_count_are_enforced()
    {
        Assert.Throws<ArgumentException>(() => Call("skill_save", Save(description: new string('a', SkillTools.MaxDescription + 1))));
        Assert.Throws<ArgumentException>(() => Call("skill_save", Save(text: new string('a', SkillTools.MaxInstructions + 1))));
        Assert.Throws<ArgumentException>(() => Call("skill_save", Save(text: "   ")));

        for (var i = 0; i < SkillTools.MaxSkills; i++) _store.Save(new Skill("s" + i, "d", "t"));
        Assert.Throws<ArgumentException>(() => Call("skill_save", Save()));
        Assert.StartsWith("Zaktualizowano", Call("skill_save", Save(name: "s1")));  // edycja istniejącego nie jest ograniczona liczbą
    }

    [Fact]
    public void The_system_note_lists_names_and_descriptions_but_not_the_instructions()
    {
        Assert.Null(Tools.SystemNote);

        _store.Save(new Skill("opis-produktu", "Pisanie\nopisów produktów", "TAJNA TRESC"));
        var note = Tools.SystemNote!;

        Assert.Contains("- opis-produktu: Pisanie opisów produktów", note);
        Assert.Contains("skill_load", note);
        Assert.DoesNotContain("TAJNA TRESC", note);
    }

    [Fact]
    public void Saving_and_deleting_need_consent_but_reading_does_not()
    {
        var writes = Tools.Tools.Where(t => t.Writes).Select(t => t.Name).Order().ToList();

        Assert.Equal(["skill_delete", "skill_save"], writes);
    }

    [Fact]
    public async Task The_registry_adds_the_group_and_a_write_without_chat_consent_asks_the_user()
    {
        var asked = new List<string>();
        var tools = BuiltinToolRegistry.Create(new MemoryMemoryStore(), Path.Combine(Path.GetTempPath(), "ws-skills"), call => { asked.Add(call.Name); return false; },
                                               skills: _store);

        Assert.Contains(tools.Groups, g => g.Id == "skills");
        var refused = await tools.CallAsync("skill_save", Save(), alreadyApproved: false);

        Assert.Equal(["skill_save"], asked);
        Assert.Equal("Użytkownik odrzucił zapis.", refused);
        Assert.Empty(_store.List());
        Assert.StartsWith("Utworzono", await tools.CallAsync("skill_save", Save(), alreadyApproved: true));
    }

    [Fact]
    public void The_group_is_absent_when_the_app_has_no_skill_store()
    {
        var tools = BuiltinToolRegistry.Create(new MemoryMemoryStore(), Path.Combine(Path.GetTempPath(), "ws-skills"), _ => true);

        Assert.DoesNotContain(tools.Groups, g => g.Id == "skills");
    }

    [Fact]
    public async Task The_app_exposes_skill_tools_in_the_chat_and_a_saved_skill_reaches_the_system_note()
    {
        using var app = TestApp.Create();
        var tools = app.Vm.Tools;
        tools.UseTools = true;

        Assert.Contains(tools.Tools, t => t.Name == "builtin__skill_save");
        Assert.Contains(tools.ToolGroups, g => g.Title == "Skile");
        Assert.True(tools.HasSkills);
        Assert.Null(tools.SystemNote?.Contains("opis-produktu") == true ? "x" : null);

        app.Vm.Tools.Permissions.Allow("builtin__skill_save", ApprovalChoice.Session);
        var result = await tools.CallAsync(new ToolCall("1", "builtin__skill_save", Save()));

        Assert.StartsWith("Utworzono", result);
        Assert.Contains("opis-produktu", tools.SystemNote);
        Assert.Equal("opis-produktu", app.Get<ISkillStore>().List().Single().Name);
    }
}

public sealed class FileSkillStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "skills-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void A_skill_round_trips_through_a_markdown_file_with_a_header()
    {
        var store = new FileSkillStore(_dir);

        store.Save(new Skill("opis-produktu", "Pisanie opisów", "Linia 1\n\nLinia 2"));

        var text = File.ReadAllText(Path.Combine(_dir, "opis-produktu", "SKILL.md"));
        Assert.StartsWith("---\nname: opis-produktu\ndescription: Pisanie opisów\n---\n", text);
        var loaded = store.Get("opis-produktu")!;
        Assert.Equal(("Pisanie opisów", "Linia 1\n\nLinia 2"), (loaded.Description, loaded.Instructions));
        Assert.Equal(["opis-produktu"], store.List().Select(s => s.Name));
    }

    [Fact]
    public void A_hand_edited_file_with_crlf_and_quotes_is_read_and_a_file_without_header_is_all_text()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "reczny"));
        File.WriteAllText(Path.Combine(_dir, "reczny", "SKILL.md"), "---\r\nname: reczny\r\ndescription: \"Opis w cudzysłowie\"\r\n---\r\nTreść\r\ndruga linia\r\n");
        Directory.CreateDirectory(Path.Combine(_dir, "bez-naglowka"));
        File.WriteAllText(Path.Combine(_dir, "bez-naglowka", "SKILL.md"), "Sama treść");
        var store = new FileSkillStore(_dir);

        Assert.Equal(("Opis w cudzysłowie", "Treść\ndruga linia"), (store.Get("reczny")!.Description, store.Get("reczny")!.Instructions));
        Assert.Equal(("", "Sama treść"), (store.Get("bez-naglowka")!.Description, store.Get("bez-naglowka")!.Instructions));
    }

    [Fact]
    public void Names_that_escape_the_folder_are_rejected_and_stray_folders_are_ignored()
    {
        var store = new FileSkillStore(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "Zly Folder"));
        File.WriteAllText(Path.Combine(_dir, "Zly Folder", "SKILL.md"), "x");

        Assert.Throws<ArgumentException>(() => store.Save(new Skill("../poza", "d", "t")));
        Assert.Null(store.Get("../poza"));
        Assert.False(store.Delete("../poza"));
        Assert.Empty(store.List());
        Assert.False(Directory.Exists(Path.Combine(_dir, "..", "poza")));
    }

    [Fact]
    public void Delete_removes_the_file_but_keeps_a_folder_with_other_files()
    {
        var store = new FileSkillStore(_dir);
        store.Save(new Skill("a", "d", "t"));
        store.Save(new Skill("b", "d", "t"));
        File.WriteAllText(Path.Combine(_dir, "b", "notatki.txt"), "moje");

        Assert.True(store.Delete("a"));
        Assert.True(store.Delete("b"));

        Assert.False(Directory.Exists(Path.Combine(_dir, "a")));
        Assert.True(File.Exists(Path.Combine(_dir, "b", "notatki.txt")));
        Assert.False(store.Delete("a"));
    }

    [Fact]
    public void A_missing_root_means_no_skills()
    {
        Assert.Empty(new FileSkillStore(_dir).List());
    }
}
