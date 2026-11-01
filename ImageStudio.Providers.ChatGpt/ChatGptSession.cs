using System.Text.Json;

namespace ImageStudio.Services;

// Sesja "Sign in with ChatGPT": co aplikacja zapamiętuje po rejestracji i logowaniu (jeden rekord JSON w SecretStore).
// HostId: stały identyfikator tej instalacji (ext_agent_host_id); ClientId: nadany przez OpenAI przy pierwszym logowaniu
// (po wylogowaniu zostają oba, a tokeny znikają). Refresh token się obraca: po każdym odświeżeniu trzeba zapisać nowy
public sealed record ChatGptSession(string HostId, string? ClientId = null, string? AccessToken = null,
                                    string? RefreshToken = null, DateTimeOffset? ExpiresAt = null,
                                    string? Email = null, string? Scope = null)
{
    // Zakres, bez którego plan ChatGPT nie może być użyty do zapytań
    public const string PlanScope = "chatgpt.tokens.use.direct";

    public static ChatGptSession New() => new("urn:uuid:" + Guid.NewGuid());

    // Zalogowany = jest czym odnowić dostęp (refresh token) albo ważny access token
    public bool IsSignedIn => !string.IsNullOrEmpty(RefreshToken) || !string.IsNullOrEmpty(AccessToken);

    public bool PlanEnabled => Scope?.Split(' ').Contains(PlanScope) == true;

    public ChatGptSession SignedOut() => this with { AccessToken = null, RefreshToken = null, ExpiresAt = null, Email = null, Scope = null };

    public string ToJson() => JsonSerializer.Serialize(this);

    // Pusty albo uszkodzony zapis = nowa, niezalogowana sesja (nie blokuje aplikacji)
    public static ChatGptSession Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return New();
        try
        {
            var session = JsonSerializer.Deserialize<ChatGptSession>(json);
            return session is { HostId.Length: > 0 } ? session : New();
        }
        catch (JsonException)
        {
            return New();
        }
    }
}
