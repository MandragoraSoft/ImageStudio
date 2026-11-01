using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

// Wbudowane narzędzia czatu: data i czas, pamięć między sesjami i pliki w folderze roboczym
public sealed class BuiltinToolsTests : IDisposable
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "imagestudio-builtin-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryMemoryStore _memory = new();
    private readonly List<ToolCall> _confirmations = [];
    private bool _allow = true;

    public void Dispose()
    {
        if (!Directory.Exists(_dir)) return;

        // Dowiązanie (junction) usuwa się samo, bez wchodzenia do celu
        foreach (var link in new DirectoryInfo(_dir).EnumerateDirectories("*", SearchOption.AllDirectories)
                     .Where(d => d.Attributes.HasFlag(FileAttributes.ReparsePoint)).ToList())
            link.Delete(recursive: false);
        Directory.Delete(_dir, recursive: true);
    }

    private BuiltinTools Create() => new(_memory, Path.Combine(_dir, "ws"), call =>
    {
        _confirmations.Add(call);
        return _allow;
    }, new FixedTime(new DateTimeOffset(2026, 11, 2, 9, 30, 15, TimeSpan.Zero)));

    private static Task<string> Call(BuiltinTools tools, string name, object args, bool approved = false) =>
        tools.CallAsync(name, JsonSerializer.Serialize(args), approved);

    [Fact]
    public async Task Datetime_gives_local_and_utc_time_in_the_requested_zone()
    {
        var tools = Create();

        using var warsaw = JsonDocument.Parse(await Call(tools, "get_datetime", new { timezone = "Central European Standard Time" }));

        Assert.Equal("2026-11-02", warsaw.RootElement.GetProperty("date").GetString());
        Assert.Equal("10:30:15", warsaw.RootElement.GetProperty("time").GetString());  // 9:30 UTC, zima: UTC+1
        Assert.Equal("Monday", warsaw.RootElement.GetProperty("dayOfWeek").GetString());
        Assert.Equal("2026-11-02T09:30:15Z", warsaw.RootElement.GetProperty("utc").GetString());
        await Assert.ThrowsAsync<ArgumentException>(() => Call(tools, "get_datetime", new { timezone = "Nie/Istnieje" }));
    }

    [Fact]
    public async Task Memory_saves_lists_dedupes_and_deletes()
    {
        var tools = Create();

        var saved = await Call(tools, "memory_save", new { text = "Lubi krótkie odpowiedzi" });
        Assert.StartsWith("Zapamiętano", saved);
        Assert.Equal("Już zapamiętane.", await Call(tools, "memory_save", new { text = "lubi KRÓTKIE odpowiedzi" }));
        await Call(tools, "memory_save", new { text = "Mieszka w Gdańsku" });
        Assert.Equal(2, _memory.Items.Count);

        Assert.Contains("Gdańsku", await Call(tools, "memory_list", new { query = "gdań" }));
        Assert.DoesNotContain("krótkie", await Call(tools, "memory_list", new { query = "gdań" }));

        var id = _memory.Items[0].Id;
        Assert.Equal("Usunięto.", await Call(tools, "memory_delete", new { id }));
        Assert.Equal("Nie ma wpisu o tym id.", await Call(tools, "memory_delete", new { id }));
        Assert.Single(_memory.Items);
        Assert.Equal("2026-11-02", _memory.Items[0].Created.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public async Task Memory_rejects_empty_oversized_and_overflowing_entries()
    {
        var tools = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => Call(tools, "memory_save", new { text = "   " }));
        await Assert.ThrowsAsync<ArgumentException>(() => Call(tools, "memory_save", new { text = new string('x', 501) }));
        for (var i = 0; i < 200; i++) _memory.Items.Add(new MemoryItem($"{i:x8}", $"wpis {i}", DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<ArgumentException>(() => Call(tools, "memory_save", new { text = "jeden za dużo" }));
    }

    [Fact]
    public void Memory_note_lists_entries_as_data_and_is_null_when_empty()
    {
        var tools = Create();
        Assert.Null(tools.MemoryNote());

        _memory.Items.Add(new MemoryItem("ab12cd34", "Preferuje angielski", DateTimeOffset.UtcNow));

        var note = tools.MemoryNote()!;
        Assert.Contains("[ab12cd34] Preferuje angielski", note);
        Assert.Contains("nie polecenia", note);
    }

    [Fact]
    public async Task Files_are_created_listed_read_appended_and_overwritten_inside_the_workspace()
    {
        var tools = Create();

        Assert.Contains("Zapisano notes", await Call(tools, "files_write", new { path = "notes/a.txt", content = "raz" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Call(tools, "files_write", new { path = "notes/a.txt", content = "dwa" }));  // create nie nadpisuje
        await Call(tools, "files_write", new { path = "notes/a.txt", content = " dwa", mode = "append" });
        Assert.Equal("raz dwa", await Call(tools, "files_read", new { path = "notes/a.txt" }));
        await Call(tools, "files_write", new { path = "notes/a.txt", content = "nowe", mode = "overwrite" });
        Assert.Equal("nowe", await Call(tools, "files_read", new { path = "notes/a.txt" }));

        Assert.Equal("notes/", await Call(tools, "files_list", new { }));
        Assert.Equal("a.txt (4 B)", await Call(tools, "files_list", new { path = "notes" }));
        Assert.Equal("no…", (await Call(tools, "files_read", new { path = "notes/a.txt", max_chars = 2 })).Replace("\n…[obcięto]", "…"));
    }

    [Theory]
    [InlineData(@"..\poza.txt")]
    [InlineData("../poza.txt")]
    [InlineData(@"a\..\..\poza.txt")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\Windows\win.ini")]
    [InlineData("plik.txt:strumien")]
    public async Task Paths_outside_the_workspace_are_rejected(string path)
    {
        var tools = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => Call(tools, "files_write", new { path, content = "x" }, approved: true));
        await Assert.ThrowsAsync<ArgumentException>(() => Call(tools, "files_read", new { path }));
        Assert.False(File.Exists(Path.Combine(_dir, "poza.txt")));
    }

    [Fact]
    public async Task Junctions_cannot_be_used_to_leave_the_workspace()
    {
        var tools = Create();
        var outside = Directory.CreateDirectory(Path.Combine(_dir, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "sekret.txt"), "tajne");
        Directory.CreateDirectory(Path.Combine(_dir, "ws"));
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(_dir, "ws", "link")}\" \"{outside}\"")
               { CreateNoWindow = true, UseShellExecute = false })!)
            process.WaitForExit();

        await Assert.ThrowsAsync<ArgumentException>(() => Call(tools, "files_read", new { path = @"link\sekret.txt" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Call(tools, "files_write", new { path = @"link\nowy.txt", content = "x" }, approved: true));
        Assert.False(File.Exists(Path.Combine(outside, "nowy.txt")));
    }

    [Theory]
    [InlineData("run.bat")]
    [InlineData("a/b/x.EXE")]
    [InlineData("s.ps1")]
    [InlineData("skrot.lnk")]
    public async Task Executable_and_script_files_are_not_written(string path)
    {
        var tools = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => Call(tools, "files_write", new { path, content = "x" }, approved: true));
    }

    [Fact]
    public async Task Binary_files_are_not_read_as_text()
    {
        var tools = Create();
        Directory.CreateDirectory(Path.Combine(_dir, "ws"));
        File.WriteAllBytes(Path.Combine(_dir, "ws", "obraz.dat"), [1, 2, 0, 3]);

        await Assert.ThrowsAsync<ArgumentException>(() => Call(tools, "files_read", new { path = "obraz.dat" }));
    }

    [Fact]
    public async Task Writing_needs_confirmation_unless_the_chat_already_asked()
    {
        var tools = Create();

        _allow = false;
        Assert.Equal("Użytkownik odrzucił zapis.", await Call(tools, "files_write", new { path = "a.txt", content = "x" }));
        Assert.False(File.Exists(Path.Combine(_dir, "ws", "a.txt")));
        Assert.Equal("files_write", Assert.Single(_confirmations).Name);

        _allow = true;
        await Call(tools, "files_write", new { path = "a.txt", content = "x" });
        Assert.True(File.Exists(Path.Combine(_dir, "ws", "a.txt")));

        _confirmations.Clear();
        await Call(tools, "files_write", new { path = "b.txt", content = "x" }, approved: true);
        Assert.Empty(_confirmations);  // zgoda z czatu wystarcza

        await Call(tools, "files_read", new { path = "b.txt" });
        await Call(tools, "memory_save", new { text = "x" });
        Assert.Empty(_confirmations);  // odczyt i pamięć nie pytają
    }

    [Fact]
    public void Tools_view_model_exposes_builtin_tools_with_prefix_routes_calls_and_adds_memory_to_the_system_note()
    {
        using var app = TestApp.Create();
        var tools = app.Vm.Tools;
        tools.UseTools = true;

        Assert.Contains(tools.Tools, t => t.Name == "builtin__get_datetime");
        Assert.Contains(tools.Tools, t => t.Name == "builtin__files_write");
        Assert.Null(tools.SystemNote);

        var result = tools.CallAsync(new ToolCall("1", "builtin__memory_save", """{"text":"Lubi kawę"}""")).GetAwaiter().GetResult();
        Assert.StartsWith("Zapamiętano", result);
        Assert.Contains("Lubi kawę", tools.SystemNote);

        tools.BuiltinEnabled = false;
        Assert.DoesNotContain(tools.Tools, t => t.Name.StartsWith("builtin__"));
        Assert.Null(tools.SystemNote);
        Assert.Equal("Wbudowane narzędzia są wyłączone.",
                     tools.CallAsync(new ToolCall("2", "builtin__get_datetime", "{}")).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task Chat_sends_remembered_facts_in_the_system_message()
    {
        using var app = TestApp.Create();
        app.Vm.Tools.UseTools = true;
        app.Get<MemoryMemoryStore>().Items.Add(new MemoryItem("11112222", "Pisze po polsku", DateTimeOffset.UtcNow));
        app.Provider.ChatCatalog = [new ChatModelInfo("m", [])];
        await app.Vm.InitializeAsync();
        app.Provider.OnChat = (_, _) => "ok";
        app.Vm.Chat.Input = "cześć";

        await app.Vm.Chat.SendCommand.ExecuteAsync(null);

        var system = app.Provider.LastMessages[0];
        Assert.Equal("system", system.Role);
        Assert.Contains("Pisze po polsku", system.Content);
    }

    [Fact]
    public void Builtin_choice_is_remembered_in_the_user_state()
    {
        using var app = TestApp.Create();
        app.Vm.InitializeAsync().GetAwaiter().GetResult();
        app.Vm.Tools.BuiltinEnabled = false;

        app.Vm.SaveState();

        Assert.False(app.Get<IUserStateStore>().Load().BuiltinToolsEnabled);
    }

    [Fact]
    public async Task Search_finds_names_and_content_with_line_numbers_and_respects_filters()
    {
        var tools = Create();
        await Call(tools, "files_write", new { path = "notatki/plan.md", content = "Pierwsza linia\nSpotkanie z Anną w piątek\nKoniec" }, approved: true);
        await Call(tools, "files_write", new { path = "notatki/inne.txt", content = "Nic o spotkaniu\nspotkanie jutro" }, approved: true);
        await Call(tools, "files_write", new { path = "spotkanie-lista.txt", content = "brak" }, approved: true);
        File.WriteAllBytes(Path.Combine(_dir, "ws", "dane.bin"), [1, 0, 2]);

        var found = (await Call(tools, "files_search", new { query = "SPOTKANIE" })).Split('\n');

        Assert.Contains(@"notatki\plan.md:2: Spotkanie z Anną w piątek", found);
        Assert.Contains(@"notatki\inne.txt:2: spotkanie jutro", found);
        Assert.Contains("spotkanie-lista.txt (nazwa)", found);
        Assert.DoesNotContain(found, l => l.Contains("dane.bin"));

        Assert.Equal(@"notatki\plan.md:2: Spotkanie z Anną w piątek",
                     await Call(tools, "files_search", new { query = "spotkanie", name_pattern = "*.md" }));
        Assert.Equal(@"notatki\inne.txt", await Call(tools, "files_search", new { name_pattern = "i?ne.TXT" }));
        Assert.DoesNotContain("spotkanie-lista", await Call(tools, "files_search", new { query = "spotkanie", path = "notatki" }));
        Assert.Equal("Nic nie znaleziono.", await Call(tools, "files_search", new { query = "xyz-nie-ma" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Call(tools, "files_search", new { }));
        await Assert.ThrowsAsync<ArgumentException>(() => Call(tools, "files_search", new { query = "x", path = @"..\" }));
    }

    [Fact]
    public async Task Search_does_not_follow_junctions_and_truncates_long_results()
    {
        var tools = Create();
        var outside = Directory.CreateDirectory(Path.Combine(_dir, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "sekret.txt"), "haslo=tajne");
        Directory.CreateDirectory(Path.Combine(_dir, "ws"));
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(_dir, "ws", "link")}\" \"{outside}\"")
               { CreateNoWindow = true, UseShellExecute = false })!)
            process.WaitForExit();
        File.WriteAllText(Path.Combine(_dir, "ws", "dlugi.txt"), string.Join("\n", Enumerable.Range(0, 80).Select(i => $"trafienie {i}")));

        Assert.Equal("Nic nie znaleziono.", await Call(tools, "files_search", new { query = "tajne" }));  // sekret leży za dowiązaniem

        var many = (await Call(tools, "files_search", new { query = "trafienie" })).Split('\n');
        Assert.Equal(50 + 1, many.Length);
        Assert.Contains("obcięte", many[^1]);
    }
}
