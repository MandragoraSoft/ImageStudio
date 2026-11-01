using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Configuration;
using ImageStudio.Services;

namespace ImageStudio.ViewModels;

// Wynik w zakładce Głos: nagranie z syntezy mowy albo tekst z rozpoznawania. Path wskazuje plik dźwiękowy (może go nie być)
public sealed partial class VoiceItem : ObservableObject
{
    public VoiceItem(bool isSpeech, string title, string text, string? path, string model, decimal? costUsd)
    {
        IsSpeech = isSpeech;
        Title = title;
        Text = text;
        Path = path;
        Model = model;
        CostUsd = costUsd;
    }

    public bool IsSpeech { get; }
    public string Title { get; }
    public string Text { get; }
    public string? Path { get; }
    public string Model { get; }
    public decimal? CostUsd { get; }
    public DateTime Created { get; } = DateTime.Now;
    public bool HasAudio => Path is not null && File.Exists(Path);
    public string Kind => IsSpeech ? "Mowa" : "Transkrypcja";
    public string Footer => $"{Created:HH:mm:ss} · {Model}" + (CostUsd is { } c ? $" · ${c:0.####}" : "");

    [ObservableProperty]
    private bool _isPlaying;
}

// Zakładka Głos: synteza mowy (tekst -> plik dźwiękowy) i rozpoznawanie mowy (plik albo mikrofon -> tekst).
// Wyniki są na liście w prawym panelu; pliki leżą w katalogu wyjściowym. Dostawca bez dźwięku daje puste listy modeli
public sealed partial class VoiceViewModel : ObservableObject
{
    public const long MaxAudioBytes = 40L * 1024 * 1024;  // limit API rozpoznawania mowy

    // Katalog na nagrania z syntezy i z mikrofonu (podkatalog Nagrania); testy podmieniają go na tymczasowy
    public string OutputDir { get; set; }

    private readonly Func<IAiProvider> _provider;
    private readonly IDialogService _dialogs;
    private readonly IAudioPlayer _player;
    private readonly IAudioRecorder _recorder;
    private readonly Action<string, bool> _report;
    private readonly Action _reload;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly Stopwatch _recordingClock = new();
    private CancellationTokenSource? _cts;
    private string? _recordingPath;
    private string? _savedSpeechModel, _savedVoice, _savedTranscriptionModel, _savedMicrophone, _savedSpeaker;

    public VoiceViewModel(Func<IAiProvider> provider, IDialogService dialogs, IAudioPlayer player, IAudioRecorder recorder,
                          Action<string, bool> report, Action reload, string outputDir, UserState state)
    {
        (_provider, _dialogs, _player, _recorder, _report, _reload, OutputDir) = (provider, dialogs, player, recorder, report, reload, outputDir);
        _savedSpeechModel = state.VoiceSpeechModel;
        _savedVoice = state.VoiceName;
        _savedTranscriptionModel = state.VoiceTranscriptionModel;
        _savedMicrophone = state.VoiceMicrophone;
        _savedSpeaker = state.VoiceSpeaker;
        _speed = state.VoiceSpeed ?? 1.0;
        _format = state.VoiceFormat is "wav" ? "wav" : "mp3";
        _language = state.VoiceLanguage ?? "";
        _recorder.LevelChanged += level => Post(() =>
        {
            RecordLevel = level;
            RecordSeconds = (int)_recordingClock.Elapsed.TotalSeconds;
        });
        RefreshMicrophones();
        RefreshSpeakers();
    }

    // --- Mowa z tekstu ---

    public ObservableCollection<ModelOption> SpeechModels { get; } = [];
    public ObservableCollection<string> Voices { get; } = [];
    public IReadOnlyList<string> Formats { get; } = ["mp3", "wav"];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SynthesizeCommand))]
    [NotifyPropertyChangedFor(nameof(SpeechEstimateLabel))]
    private ModelOption? _selectedSpeechModel;

    [ObservableProperty]
    private string? _selectedVoice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SynthesizeCommand))]
    [NotifyPropertyChangedFor(nameof(SpeechEstimateLabel))]
    private string _speechText = "";

    [ObservableProperty]
    private double _speed;

    [ObservableProperty]
    private string _format;

    public string SpeechEstimateLabel
    {
        get
        {
            if (SelectedSpeechModel?.Pricing is not { } p) return "Brak ceny dla tego modelu";
            if (SpeechText.Length == 0) return $"${p.PriceNow:0.####} za 1000 znaków";
            var blocks = (int)Math.Ceiling(SpeechText.Length / 1000.0);  // rozliczenie za każde rozpoczęte 1000 znaków
            return $"Szacunek: ~${p.PriceNow * blocks:0.####} ({SpeechText.Length} znaków)";
        }
    }

    // --- Rozpoznawanie mowy ---

    public ObservableCollection<ModelOption> TranscriptionModels { get; } = [];
    public ObservableCollection<AudioDevice> Microphones { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TranscribeCommand))]
    [NotifyPropertyChangedFor(nameof(TranscriptionPriceLabel))]
    private ModelOption? _selectedTranscriptionModel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TranscribeCommand))]
    [NotifyPropertyChangedFor(nameof(AudioFileName))]
    private string? _audioPath;

    [ObservableProperty]
    private string _language;

    // Podpowiedź dla modelu: nazwy własne i słowa, których się spodziewamy
    [ObservableProperty]
    private string _transcriptionHint = "";

    [ObservableProperty]
    private AudioDevice? _selectedMicrophone;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TranscribeCommand), nameof(PickAudioCommand))]
    [NotifyPropertyChangedFor(nameof(RecordButtonText))]
    private bool _isRecording;

    [ObservableProperty]
    private double _recordLevel;

    [ObservableProperty]
    private int _recordSeconds;

    public string AudioFileName => AudioPath is null ? "Nie wybrano nagrania" : Path.GetFileName(AudioPath);
    public string RecordButtonText => IsRecording ? "■ Zatrzymaj nagrywanie" : "● Nagrywaj z mikrofonu";
    public string TranscriptionPriceLabel => SelectedTranscriptionModel?.Pricing is { } p
        ? $"${p.PriceNow:0.####} za minutę nagrania"
        : "Brak ceny dla tego modelu";

    // Wyjście dźwięku odtwarzacza (domyślne urządzenie albo konkretne); wybór jest zapamiętywany
    public ObservableCollection<AudioDevice> Speakers { get; } = [];

    [ObservableProperty]
    private AudioDevice? _selectedSpeaker;

    partial void OnSelectedSpeakerChanged(AudioDevice? value)
    {
        if (value is not null) _player.OutputDevice = value.Index;
    }

    // Lista urządzeń przy każdym otwarciu ustawień: sprzęt bywa podłączany i odłączany w trakcie pracy
    public void RefreshDevices()
    {
        _savedMicrophone = SelectedMicrophone?.Name ?? _savedMicrophone;
        _savedSpeaker = SelectedSpeaker?.Name ?? _savedSpeaker;
        RefreshMicrophones();
        RefreshSpeakers();
    }

    // Wybór z okna ustawień (po nazwie; nieznana nazwa nic nie zmienia)
    public void ApplyDevices(string? microphone, string? speaker)
    {
        if (Microphones.FirstOrDefault(d => d.Matches(microphone)) is { } mic) SelectedMicrophone = mic;
        if (Speakers.FirstOrDefault(d => d.Matches(speaker)) is { } output) SelectedSpeaker = output;
    }

    private void RefreshSpeakers()
    {
        Speakers.Clear();
        try
        {
            foreach (var device in _player.OutputDevices()) Speakers.Add(device);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report($"Nie udało się odczytać listy wyjść dźwięku: {ex.Message}", true);
        }

        SelectedSpeaker = Speakers.FirstOrDefault(d => d.Matches(_savedSpeaker)) ?? Speakers.FirstOrDefault();
    }

    // --- Wyniki ---

    public ObservableCollection<VoiceItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SynthesizeCommand), nameof(TranscribeCommand), nameof(CancelCommand))]
    private bool _isBusy;

    private void Post(Action action)
    {
        if (_ui is null) action();
        else _ui.Post(_ => action(), null);
    }

    private void Report(string message, bool error = false) => _report(message, error);

    // --- Katalog dostawcy ---

    // Wybory użytkownika zostają, o ile model nadal istnieje: najpierw bieżący wybór, potem zapamiętany w stanie
    public void ApplyCatalog(ProviderCatalog catalog)
    {
        if (catalog.Speech is { } speech)
        {
            var keep = SelectedSpeechModel?.Id ?? _savedSpeechModel;
            Fill(SpeechModels, speech, ["text"], ["audio"]);
            SelectedSpeechModel = SpeechModels.FirstOrDefault(m => m.Id == keep) ?? SpeechModels.FirstOrDefault();
        }

        if (catalog.Transcription is { } transcription)
        {
            var keep = SelectedTranscriptionModel?.Id ?? _savedTranscriptionModel;
            Fill(TranscriptionModels, transcription, ["audio"], ["text"]);
            SelectedTranscriptionModel = TranscriptionModels.FirstOrDefault(m => m.Id == keep) ?? TranscriptionModels.FirstOrDefault();
        }
    }

    private static void Fill(ObservableCollection<ModelOption> target, IEnumerable<AudioModelInfo> infos,
                             IReadOnlyList<string> input, IReadOnlyList<string> output)
    {
        target.Clear();
        foreach (var info in infos.OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase))
            target.Add(new ModelOption(info.Id, info.OwnedBy, false, input, info.Pricing, info.Description, output));
    }

    // Zmiana dostawcy: modele poprzedniego nie istnieją u nowego
    public void Reset()
    {
        _player.Stop();
        SpeechModels.Clear();
        TranscriptionModels.Clear();
        Voices.Clear();
        SelectedSpeechModel = null;
        SelectedTranscriptionModel = null;
    }

    [RelayCommand]
    private void ReloadModels() => _reload();

    partial void OnSelectedSpeechModelChanged(ModelOption? value) => _ = LoadVoicesAsync(value);

    // Głosy zależą od modelu (u UberLLM każdy ma własną listę); zapamiętany głos wraca, gdy model go ma
    private async Task LoadVoicesAsync(ModelOption? model)
    {
        var keep = SelectedVoice ?? _savedVoice;
        IReadOnlyList<string> voices = [];
        if (model is not null)
        {
            try
            {
                voices = await _provider().GetSpeechVoicesAsync(model.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Report($"Nie udało się pobrać głosów: {ex.Message}", true);
            }
        }

        if (SelectedSpeechModel != model) return;  // użytkownik zdążył wybrać inny model
        Voices.Clear();
        foreach (var voice in voices) Voices.Add(voice);
        SelectedVoice = Voices.Contains(keep ?? "") ? keep : Voices.FirstOrDefault();
    }

    // --- Synteza ---

    private bool CanSynthesize => !IsBusy && SelectedSpeechModel is not null && !string.IsNullOrWhiteSpace(SpeechText);

    [RelayCommand(CanExecute = nameof(CanSynthesize))]
    private async Task SynthesizeAsync()
    {
        var model = SelectedSpeechModel!;
        var text = SpeechText.Trim();
        var estimate = model.Pricing is { } p ? p.PriceNow * (decimal)Math.Ceiling(text.Length / 1000.0) : (decimal?)null;
        using var cts = Begin();
        try
        {
            Report("Synteza mowy...");
            var result = await _provider().SynthesizeAsync(model.Id, text, SelectedVoice, Math.Abs(Speed - 1.0) < 0.001 ? null : Speed,
                                                           Format, cts.Token);
            Directory.CreateDirectory(OutputDir);
            var path = Path.Combine(OutputDir, $"{DateTime.Now:yyyyMMdd-HHmmss}.{result.Extension}");
            await File.WriteAllBytesAsync(path, result.Bytes, cts.Token);

            var item = new VoiceItem(true, text.Length > 60 ? text[..60] + "..." : text, text, path, model.Id, result.CostUsd ?? estimate);
            Items.Insert(0, item);
            Report($"Zapisano nagranie: {path}");
            Play(item);
        }
        catch (OperationCanceledException)
        {
            Report("Anulowano.");
        }
        catch (Exception ex)
        {
            Report($"Błąd syntezy mowy: {ex.Message}", true);
        }
        finally
        {
            End();
        }
    }

    // --- Nagrywanie i wybór pliku ---

    [RelayCommand(CanExecute = nameof(CanPick))]
    private void PickAudio()
    {
        if (_dialogs.PickAudioFile() is { } path) AudioPath = path;
    }

    private bool CanPick => !IsRecording;

    private void RefreshMicrophones()
    {
        Microphones.Clear();
        try
        {
            foreach (var device in _recorder.Devices()) Microphones.Add(device);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report($"Nie udało się odczytać listy mikrofonów: {ex.Message}", true);
        }

        SelectedMicrophone = Microphones.FirstOrDefault(m => m.Matches(_savedMicrophone)) ?? Microphones.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (IsRecording)
        {
            await _recorder.StopAsync();
            _recordingClock.Stop();
            IsRecording = false;
            RecordLevel = 0;
            AudioPath = _recordingPath;
            Report($"Nagranie ({RecordSeconds} s) gotowe do rozpoznania.");
            return;
        }

        if (SelectedMicrophone is null)
        {
            Report("Nie znaleziono mikrofonu. Sprawdź podłączenie i Ustawienia Windows → Prywatność → Mikrofon.", true);
            return;
        }

        _player.Stop();
        _recordingPath = Path.Combine(OutputDir, "Nagrania", $"{DateTime.Now:yyyyMMdd-HHmmss}.wav");
        try
        {
            _recorder.Start(SelectedMicrophone.Index, _recordingPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report($"Nie udało się uruchomić mikrofonu: {ex.Message}. Sprawdź Ustawienia Windows → Prywatność → Mikrofon.", true);
            return;
        }

        RecordSeconds = 0;
        _recordingClock.Restart();
        IsRecording = true;
        Report("Nagrywanie...");
    }

    // --- Rozpoznawanie ---

    private bool CanTranscribe => !IsBusy && !IsRecording && SelectedTranscriptionModel is not null && !string.IsNullOrEmpty(AudioPath);

    [RelayCommand(CanExecute = nameof(CanTranscribe))]
    private async Task TranscribeAsync()
    {
        var model = SelectedTranscriptionModel!;
        var path = AudioPath!;
        if (!File.Exists(path))
        {
            Report($"Nie ma pliku: {path}", true);
            return;
        }

        if (new FileInfo(path).Length > MaxAudioBytes)
        {
            Report("Plik jest większy niż 40 MB (limit API). Wybierz krótsze nagranie.", true);
            return;
        }

        using var cts = Begin();
        try
        {
            Report("Rozpoznawanie mowy...");
            var result = await _provider().TranscribeAsync(model.Id, path, Language, TranscriptionHint, cts.Token);
            var text = result.Text.Trim();
            Items.Insert(0, new VoiceItem(false, Path.GetFileName(path), text.Length == 0 ? "(brak rozpoznanego tekstu)" : text, path, model.Id, result.CostUsd));
            Report("Rozpoznano tekst.");
        }
        catch (OperationCanceledException)
        {
            Report("Anulowano.");
        }
        catch (Exception ex)
        {
            Report($"Błąd rozpoznawania mowy: {ex.Message}", true);
        }
        finally
        {
            End();
        }
    }

    private CancellationTokenSource Begin()
    {
        IsBusy = true;
        return _cts = new CancellationTokenSource();
    }

    private void End()
    {
        _cts = null;
        IsBusy = false;
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cts?.Cancel();

    // --- Wyniki: odtwarzanie, zapis, kopiowanie ---

    [RelayCommand]
    private void Play(VoiceItem? item)
    {
        if (item?.Path is null || !item.HasAudio) return;

        StopPlayback();
        item.IsPlaying = true;
        _player.Play(item.Path, () => Post(() => item.IsPlaying = false),
                     reason => Post(() => Report($"Nie udało się odtworzyć nagrania: {reason}. Użyj „Otwórz w odtwarzaczu”.", true)));
    }

    // Zapasowa droga: domyślny odtwarzacz systemu (gdy wbudowany nie radzi sobie z plikiem albo urządzeniem dźwiękowym)
    [RelayCommand]
    private void OpenExternally(VoiceItem? item)
    {
        if (item?.Path is null || !item.HasAudio) return;

        StopPlayback();
        try
        {
            _dialogs.OpenFile(item.Path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Report($"Nie udało się otworzyć pliku: {ex.Message}", true);
        }
    }

    [RelayCommand]
    private void StopPlayback()
    {
        _player.Stop();
        foreach (var item in Items) item.IsPlaying = false;
    }

    [RelayCommand]
    private void SaveAs(VoiceItem? item)
    {
        if (item is null) return;

        if (item.IsSpeech && item.Path is not null)
        {
            var ext = Path.GetExtension(item.Path);
            if (_dialogs.ChooseSaveFileAs(Path.GetFileName(item.Path), "Zapisz nagranie", $"Dźwięk|*{ext}") is { } target)
                CopyFile(item.Path, target);
        }
        else if (_dialogs.ChooseSaveFileAs($"transkrypcja-{item.Created:yyyyMMdd-HHmmss}.txt", "Zapisz tekst", "Tekst|*.txt") is { } target)
        {
            try
            {
                File.WriteAllText(target, item.Text);
                Report($"Zapisano: {target}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Report($"Nie udało się zapisać pliku: {ex.Message}", true);
            }
        }
    }

    private void CopyFile(string source, string target)
    {
        try
        {
            File.Copy(source, target, overwrite: true);
            Report($"Zapisano: {target}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report($"Nie udało się zapisać pliku: {ex.Message}", true);
        }
    }

    [RelayCommand]
    private void CopyText(VoiceItem? item)
    {
        if (item is null) return;
        _dialogs.CopyText(item.Text);
        Report("Skopiowano tekst.");
    }

    // Tekst wyniku trafia do pola syntezy (np. transkrypcja do ponownego odczytania)
    [RelayCommand]
    private void UseAsSpeechText(VoiceItem? item)
    {
        if (item is not null) SpeechText = item.Text;
    }

    [RelayCommand]
    private void Remove(VoiceItem? item)
    {
        if (item is null) return;
        if (item.IsPlaying) StopPlayback();
        Items.Remove(item);
    }

    // Wybory do zapisania w UserState
    public void Capture(UserState state)
    {
        state.VoiceSpeechModel = SelectedSpeechModel?.Id ?? state.VoiceSpeechModel;
        state.VoiceName = SelectedVoice ?? state.VoiceName;
        state.VoiceSpeed = Speed;
        state.VoiceFormat = Format;
        state.VoiceTranscriptionModel = SelectedTranscriptionModel?.Id ?? state.VoiceTranscriptionModel;
        state.VoiceLanguage = Language;
        state.VoiceMicrophone = SelectedMicrophone?.Name ?? state.VoiceMicrophone;
        state.VoiceSpeaker = SelectedSpeaker?.Name ?? state.VoiceSpeaker;
    }
}
