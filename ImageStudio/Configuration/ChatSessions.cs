using System.IO;
using System.Text.Json;

namespace ImageStudio.Configuration;

// Wiersz listy sesji (plik meta: lista nie czyta całych rozmów z obrazami)
public sealed record ChatSessionInfo(string Id, string Title, DateTime UpdatedUtc)
{
    public string UpdatedLabel => UpdatedUtc.ToLocalTime().ToString("dd.MM HH:mm");
}

public sealed record ChatImageData(string Mime, string Base64);

// Wiadomość zapisana w sesji; rozumowanie i zużycie tokenów też, żeby po otwarciu rozmowa wyglądała tak samo
public sealed class ChatMessageData
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public string? Reasoning { get; set; }
    public List<Services.ToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public Services.TokenUsage? Usage { get; set; }
    public List<ChatImageData>? Images { get; set; }
}

public sealed class ChatSessionData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? Model { get; set; }
    public List<ChatMessageData> Messages { get; set; } = [];
}

// Sesje czatu w %LOCALAPPDATA%\ImageStudio\sessions: <id>.json (rozmowa) i <id>.meta.json (tytuł i czas, do listy).
// Uszkodzone pliki są pomijane; błąd zapisu nie przerywa pracy czatu
public sealed class FileChatSessionStore : IChatSessionStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private static string Dir => Path.Combine(AppPaths.UserDataDir, "sessions");

    // Id trafia do nazwy pliku: tylko znaki szesnastkowe (nie da się wyjść poza katalog sesji)
    private static bool ValidId(string id) => id.Length is > 0 and <= 64 && id.All(Uri.IsHexDigit);

    public IReadOnlyList<ChatSessionInfo> List()
    {
        if (!Directory.Exists(Dir)) return [];

        var result = new List<ChatSessionInfo>();
        foreach (var file in Directory.EnumerateFiles(Dir, "*.meta.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<ChatSessionInfo>(File.ReadAllText(file)) is { } info && ValidId(info.Id)) result.Add(info);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // pomijamy uszkodzony wpis
            }
        }

        return result.OrderByDescending(i => i.UpdatedUtc).ToList();
    }

    public ChatSessionData? Load(string id)
    {
        if (!ValidId(id)) return null;
        var path = Path.Combine(Dir, id + ".json");
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<ChatSessionData>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    public void Save(ChatSessionData session)
    {
        if (!ValidId(session.Id)) return;

        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, session.Id + ".json"), JsonSerializer.Serialize(session, Options));
            File.WriteAllText(Path.Combine(Dir, session.Id + ".meta.json"),
                              JsonSerializer.Serialize(new ChatSessionInfo(session.Id, session.Title, session.UpdatedUtc), Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Zapis sesji jest wygodą: czat działa dalej
        }
    }

    public void Delete(string id)
    {
        if (!ValidId(id)) return;

        try
        {
            foreach (var name in new[] { id + ".json", id + ".meta.json" })
            {
                var path = Path.Combine(Dir, name);
                if (File.Exists(path)) File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // jak wyżej
        }
    }
}
