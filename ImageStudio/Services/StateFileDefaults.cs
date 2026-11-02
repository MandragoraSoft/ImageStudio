using ImageStudio.Configuration;

namespace ImageStudio.Services;

// Domyślne ustawienia czytane i zapisywane wprost w state.json (tryb bez okna); dostawca pochodzi z settings.json (Ai.Provider).
// To te same pola, które zapisuje okno, więc po wybraniu modelu tutaj okno pokaże go przy następnym starcie.
public sealed class StateFileDefaults(IUserStateStore store, string providerId) : IStudioDefaults
{
    public string ProviderId => providerId;

    public string? Model(string kind)
    {
        var state = store.Load();
        return kind switch
        {
            "generate" => state.GenerationModel,
            "edit" => state.EditModel,
            "video" => state.VideoModel,
            "speech" => state.VoiceSpeechModel,
            "transcription" => state.VoiceTranscriptionModel,
            "chat" => state.EnhancerModel,
            _ => null,
        };
    }

    public void SetModel(string kind, string id)
    {
        var state = store.Load();
        switch (kind)
        {
            case "generate": state.GenerationModel = id; break;
            case "edit": state.EditModel = id; break;
            case "video": state.VideoModel = id; break;
            case "speech": state.VoiceSpeechModel = id; break;
            case "transcription": state.VoiceTranscriptionModel = id; break;
            case "chat": state.EnhancerModel = id; break;
            default: throw new ArgumentException($"Nieznany rodzaj: {kind}.");
        }

        store.Save(state);
    }
}
