namespace ImageStudio.Configuration;

// Gdzie zapisywana jest rozmowa z edytora promptu: na dysku (także po restarcie), tylko do zamknięcia aplikacji, albo wcale
public enum PromptChatHistoryMode
{
    Disk = 0,
    UntilClose = 1,
    Off = 2,
}

// Rozmowy edytora trzymane w pamięci (tryb UntilClose): znikają razem z aplikacją, nic nie trafia na dysk
public sealed class InMemoryPromptChatStore : IPromptChatSessionStore
{
    private readonly Dictionary<string, ChatSessionData> _sessions = [];

    public IReadOnlyList<ChatSessionInfo> List() =>
        _sessions.Values.OrderByDescending(s => s.UpdatedUtc).Select(s => new ChatSessionInfo(s.Id, s.Title, s.UpdatedUtc)).ToList();

    public ChatSessionData? Load(string id) => _sessions.GetValueOrDefault(id);

    public void Save(ChatSessionData session) => _sessions[session.Id] = session;

    public void Delete(string id) => _sessions.Remove(id);
}
