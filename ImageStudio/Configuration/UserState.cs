using System.IO;
using System.Text.Json;

namespace ImageStudio.Configuration;

public sealed class ParamState
{
    public bool Enabled { get; set; }
    public double Value { get; set; }
}

// Ostatnio wybrane modele i parametry; zapis w %LOCALAPPDATA%\ImageStudio\state.json
public sealed class UserState
{
    // Ostatnio wpisane prompty (przywracane po starcie); zestawy zapisane osobno jako Preset
    public string? GeneratePrompt { get; set; }
    public string? EditPrompt { get; set; }
    public string? GenerateSystemPrompt { get; set; }
    public string? EditSystemPrompt { get; set; }
    public string? GenerationModel { get; set; }
    public string? EditModel { get; set; }
    public string? EnhancerModel { get; set; }
    public string? ChatModel { get; set; }
    public string? ChatSystemPrompt { get; set; }
    public bool? ChatReasoningEnabled { get; set; }
    public string? ChatReasoningEffort { get; set; }
    // Zakładka Głos: ostatnie wybory (model mowy, głos, prędkość, format, model rozpoznawania, język, mikrofon)
    public string? VoiceSpeechModel { get; set; }
    public string? VoiceName { get; set; }
    public double? VoiceSpeed { get; set; }
    public string? VoiceFormat { get; set; }
    public string? VoiceTranscriptionModel { get; set; }
    public string? VoiceLanguage { get; set; }
    public string? VoiceMicrophone { get; set; }
    public string? VoiceSpeaker { get; set; }
    public string? VoiceSpeechLanguage { get; set; }
    public bool UseTools { get; set; }
    public bool? RequireToolApproval { get; set; }
    public List<string> McpEnabled { get; set; } = [];
    public bool AppControlEnabled { get; set; }
    // Wbudowane narzędzia czatu (data i czas, pamięć, pliki); null = domyślnie włączone
    public bool? BuiltinToolsEnabled { get; set; }
    // Narzędzia czatu wyłączone przez użytkownika (nazwy widoczne dla modelu); null = pierwsze uruchomienie, wartości z flag wyżej
    public List<string>? DisabledTools { get; set; }
    public bool McpServerEnabled { get; set; }
    public int? McpServerPort { get; set; }
    public string? EnhancerInstruction { get; set; }
    // Kolejność zakładek lewego panelu po przeciągnięciu (nazwy typów zakładek); null = domyślna
    public List<string>? TabOrder { get; set; }
    public string? SeedText { get; set; }
    public string? NegativePrompt { get; set; }
    public string? Quality { get; set; }
    public Dictionary<string, ParamState> EnhancerParams { get; set; } = [];

    private static string FilePath => Path.Combine(AppPaths.UserDataDir, "state.json");

    public static UserState Load()
    {
        if (!File.Exists(FilePath)) return new UserState();

        try
        {
            return JsonSerializer.Deserialize<UserState>(File.ReadAllText(FilePath)) ?? new UserState();
        }
        catch (JsonException)
        {
            // Uszkodzony stan nie blokuje startu; użyte będą wartości domyślne
            return new UserState();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.UserDataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
