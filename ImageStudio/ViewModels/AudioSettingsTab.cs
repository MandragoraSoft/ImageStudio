using CommunityToolkit.Mvvm.ComponentModel;
using ImageStudio.Services;

namespace ImageStudio.ViewModels;

// Zakładka "Dźwięk": domyślne urządzenia wejścia (mikrofon) i wyjścia (głośniki, słuchawki) aplikacji.
// Urządzenia są rozpoznawane po nazwie, bo numery winmm zmieniają się po podłączeniu i odłączeniu sprzętu;
// brak zapisanego urządzenia (odłączone) wraca do pierwszej pozycji, czyli domyślnego urządzenia systemu
public sealed partial class AudioSettingsTab : ObservableObject, ISettingsTab
{
    public string Title => "Dźwięk";
    public int Order => 5;

    public IReadOnlyList<AudioDevice> Inputs { get; }
    public IReadOnlyList<AudioDevice> Outputs { get; }

    [ObservableProperty]
    private AudioDevice? _selectedInput;

    [ObservableProperty]
    private AudioDevice? _selectedOutput;

    public AudioSettingsTab(SettingsResult current, IReadOnlyList<AudioDevice> inputs, IReadOnlyList<AudioDevice> outputs)
    {
        Inputs = inputs;
        Outputs = outputs;
        _selectedInput = inputs.FirstOrDefault(d => d.Matches(current.Microphone)) ?? inputs.FirstOrDefault();
        _selectedOutput = outputs.FirstOrDefault(d => d.Matches(current.Speaker)) ?? outputs.FirstOrDefault();
    }

    public SettingsResult Apply(SettingsResult result) =>
        result with { Microphone = SelectedInput?.Name ?? result.Microphone, Speaker = SelectedOutput?.Name ?? result.Speaker };
}
