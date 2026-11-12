namespace ImageStudio.Services;

// Rozmowa edytora nie może zmieniać głównego promptu przed zatwierdzeniem wyniku.
internal sealed class PromptConversationTools(IToolHost inner) : IToolHost
{
    private const string Blocked = AppControlTools.ChatPrefix + "set_prompt";

    public IReadOnlyList<ToolDefinition> Tools => inner.Tools.Where(t => t.Name != Blocked).ToList();
    public string? SystemNote => inner.SystemNote;
    public bool RequireApproval => inner.RequireApproval;

    public Task<string> CallAsync(ToolCall call, CancellationToken ct = default)
    {
        if (call.Name == Blocked)
            return Task.FromResult("Narzędzie set_prompt jest niedostępne w rozmowie edytora promptu.");
        if (!inner.Tools.Any(tool => tool.Name == call.Name))
            return Task.FromResult("Narzędzie jest niedostępne lub wyłączone przez użytkownika.");
        return inner.CallAsync(call, ct);
    }
}
