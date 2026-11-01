using System.Text.Json;

namespace ImageStudio.Services;

// Wbudowane narzędzia czatu, niezależne od serwerów MCP. Każda grupa (data i czas, pamięć, pliki) leży w osobnej bibliotece
// ImageStudio.Tools.* i implementuje IChatToolGroup; tu są składane (BuiltinToolRegistry), kierowane po nazwie i chronione zgodą.
// W czacie nazwy dostają przedrostek ChatPrefix. Zapis (ChatToolInfo.Writes) wymaga zgody użytkownika, jeśli czat jej nie zebrał
public sealed class BuiltinTools
{
    public const string ChatPrefix = "builtin__";

    private readonly Func<ToolCall, bool> _confirmWrite;
    private readonly Dictionary<string, (IChatToolGroup Group, ChatToolInfo Tool)> _tools = [];
    private readonly IReadOnlyList<IChatToolGroup> _groups;

    public BuiltinTools(IReadOnlyList<IChatToolGroup> groups, string workspaceRoot, Func<ToolCall, bool> confirmWrite)
    {
        _groups = groups;
        WorkspaceRoot = workspaceRoot;
        _confirmWrite = confirmWrite;
        foreach (var group in groups)
            foreach (var tool in group.Tools)
                if (!_tools.TryAdd(tool.Name, (group, tool)))
                    throw new InvalidOperationException($"Narzędzie {tool.Name} jest zdefiniowane w dwóch grupach.");
    }

    // Grupy narzędzi (do listy w panelu narzędzi czatu)
    public IReadOnlyList<IChatToolGroup> Groups => _groups;

    // Folder roboczy narzędzi plikowych (do otwarcia z panelu Narzędzia)
    public string WorkspaceRoot { get; }

    public IReadOnlyList<ToolDefinition> Definitions =>
        _tools.Values.Select(t => new ToolDefinition(t.Tool.Name, t.Tool.Description, t.Tool.Schema)).ToList();

    // Teksty grup do dopisania do instrukcji systemowej (np. zapamiętane informacje); null, gdy nie ma żadnego
    public string? SystemNote => _groups.Select(g => g.SystemNote).Where(n => !string.IsNullOrWhiteSpace(n)) is var notes && notes.Any()
        ? string.Join("\n\n", notes)
        : null;

    // Notatka tylko z grup, które mają choć jedno włączone narzędzie (isEnabled dostaje nazwę z przedrostkiem, jak widzi ją model)
    public string? SystemNoteFor(Func<string, bool> isEnabled)
    {
        var notes = _groups.Where(g => g.Tools.Any(t => isEnabled(ChatPrefix + t.Name))).Select(g => g.SystemNote)
            .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        return notes.Count > 0 ? string.Join("\n\n", notes) : null;
    }

    // alreadyApproved: użytkownik zatwierdził to wywołanie w czacie, więc zapis nie pyta drugi raz
    public Task<string> CallAsync(string name, string argumentsJson, bool alreadyApproved)
    {
        if (!_tools.TryGetValue(name, out var entry)) throw new ArgumentException($"Nieznane narzędzie: {name}");

        if (entry.Tool.Writes && !alreadyApproved && !_confirmWrite(new ToolCall("", name, argumentsJson)))
            return Task.FromResult("Użytkownik odrzucił zapis.");

        using var arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        return Task.FromResult(entry.Group.Call(name, arguments.RootElement.Clone()));
    }
}
