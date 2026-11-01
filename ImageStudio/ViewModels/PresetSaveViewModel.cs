using ImageStudio.Localization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ImageStudio.ViewModels;

public sealed partial class PresetSaveViewModel : ObservableObject
{
    private readonly Func<Task<PresetDraft>>? _suggest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _name;

    [ObservableProperty]
    private string _description = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SuggestCommand))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isSuggesting;

    // Przycisk "Zapisz": nazwa nie może być pusta, a w trakcie propozycji AI pola jeszcze się zmienią
    public bool CanSave => !string.IsNullOrWhiteSpace(Name) && !IsSuggesting;

    // Prompt zestawu (tylko podgląd) i widoczność przycisku "Zaproponuj (AI)"
    public string Prompt { get; }
    public bool CanSuggestVisible => _suggest is not null;

    public PresetSaveViewModel(string defaultName, string prompt = "", Func<Task<PresetDraft>>? suggest = null)
    {
        _name = defaultName;
        Prompt = prompt;
        _suggest = suggest;
    }

    private bool CanSuggest => _suggest is not null && !IsSuggesting;

    [RelayCommand(CanExecute = nameof(CanSuggest))]
    private async Task SuggestAsync()
    {
        IsSuggesting = true;
        Status = Loc.T("presetSave.msg.thinking");
        try
        {
            var draft = await _suggest!();
            Name = draft.Name;
            Description = draft.Description;
            Status = "";
        }
        catch (Exception ex)
        {
            Status = Loc.T("presetSave.msg.failed", ex.Message);
        }
        finally
        {
            IsSuggesting = false;
        }
    }

    // Odpowiedź modelu: {"name": "...", "description": "..."}; bywa w blokach ```json
    public static PresetDraft ParseDraft(string content)
    {
        var json = content.Trim().Trim('`').Trim();
        if (json.StartsWith("json", StringComparison.OrdinalIgnoreCase)) json = json[4..].Trim();

        string? name;
        string? description = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            name = doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            description = doc.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Nie-JSON: pierwsza linia odpowiedzi jest nazwą
            name = json.Split('\n')[0].Trim().Trim('"');
        }

        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException(Loc.T("presetSave.err.noName"));
        return new PresetDraft(name.Trim(), description?.Trim() ?? "");
    }
}
