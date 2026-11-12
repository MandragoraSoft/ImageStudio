using System.Text.Json;

namespace ImageStudio.Services;

// Rozmowa edytora nie udostępnia narzędzi, które mogą odczytać prompty innych zakładek.
internal sealed class PromptConversationTools(IToolHost inner, Func<string> editorContext) : IToolHost
{
    private const string State = AppControlTools.ChatPrefix + "get_state";
    private static readonly HashSet<string> Allowed =
    [
        State,
        AppControlTools.ChatPrefix + "list_models",
        AppControlTools.ChatPrefix + "get_defaults",
        AppControlTools.ChatPrefix + "rebuild_prompt",
        BuiltinTools.ChatPrefix + "get_datetime",
    ];

    public IReadOnlyList<ToolDefinition> Tools => inner.Tools.Where(t => Allowed.Contains(t.Name))
        .Select(t => t.Name == State ? t with { Description = "Zwraca bieżący kontekst edytora promptu, bez promptów innych zakładek." } : t)
        .ToList();
    public string? SystemNote => null;
    public bool RequireApproval => inner.RequireApproval;

    public Task<string> CallAsync(ToolCall call, CancellationToken ct = default)
    {
        if (!Allowed.Contains(call.Name))
            return Task.FromResult("Narzędzie jest niedostępne w izolowanej rozmowie edytora promptu.");
        if (!inner.Tools.Any(tool => tool.Name == call.Name))
            return Task.FromResult("Narzędzie jest niedostępne lub wyłączone przez użytkownika.");
        if (call.Name == State)
        {
            using var arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            return Task.FromResult(editorContext());
        }
        return inner.CallAsync(call, ct);
    }
}
