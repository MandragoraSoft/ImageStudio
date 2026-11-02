using ImageStudio.Localization;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using ImageStudio.Configuration;
using ImageStudio.Services;

namespace ImageStudio.ViewModels;

// Sterowanie aplikacją z zewnątrz (model w czacie i klienci MCP): te same operacje, które wykonuje użytkownik w oknie
public sealed partial class MainViewModel : IAppControl, IStudioLibrary
{
    private static readonly string[] TabNames = ["generate", "edit", "video", "chat", "voice"];

    // Narzędzia dotykają widoków, więc połączenia z wątków serwera wracają na wątek interfejsu
    // Kontekst przechwycony w konstruktorze (wątek UI); bez kontekstu (testy) akcja idzie od razu
    private Task<string> RunOnUi(Func<Task<string>> action)
    {
        if (_uiContext is null) return action();

        var done = new TaskCompletionSource<string>();
        _uiContext.Post(async _ =>
        {
            try { done.SetResult(await action()); }
            catch (Exception ex) { done.SetException(ex); }
        }, null);
        return done.Task;
    }

    AppState IAppControl.GetState() => new(
        TabNames[Math.Clamp(ActiveTab, 0, TabNames.Length - 1)], _provider.DisplayName,
        SelectedModel?.Id, SelectedEditModel?.Id, SelectedVideoModel?.Id,
        GeneratePrompt, EditPrompt, VideoPrompt, Size, References.Count, IsBusy, Status);

    IReadOnlyList<string> IAppControl.AvailableKinds() =>
        new[] { "generate", "edit", "video" }.Where(kind => ModelsFor(kind).Count > 0).ToList();

    IReadOnlyList<ModelSummary> IAppControl.ListModels(string kind, string? query, int limit)
    {
        var preferred = _prefs.For(_provider.Id).Preferred;
        return ModelsFor(kind)
            .Where(m => string.IsNullOrWhiteSpace(query) || m.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => preferred.Contains(m.Id))
            .Take(limit)
            .Select(m => new ModelSummary(m.Id, m.OwnedBy, m.PriceText, m.AcceptsImage, preferred.Contains(m.Id)))
            .ToList();
    }

    void IAppControl.SelectModel(string kind, string id)
    {
        var model = ModelsFor(kind).FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Nie ma modelu \"{id}\" dla rodzaju {kind}. Użyj list_models.");
        switch (kind)
        {
            case "generate": SelectedModel = model; break;
            case "edit": SelectedEditModel = model; break;
            default: SelectedVideoModel = model; break;
        }
    }

    void IAppControl.SetPrompt(string kind, string prompt, string? systemPrompt)
    {
        switch (kind)
        {
            case "generate":
                GeneratePrompt = prompt;
                if (systemPrompt is not null) GenerateSystemPrompt = systemPrompt;
                break;
            case "edit":
                EditPrompt = prompt;
                if (systemPrompt is not null) EditSystemPrompt = systemPrompt;
                break;
            default:
                VideoPrompt = prompt;
                break;
        }
    }

    void IAppControl.SetSize(int width, int height) => SetDimensions(width, height);

    void IAppControl.SetVideoOptions(int? durationSeconds, string? aspectRatio)
    {
        if (aspectRatio is { Length: > 0 } && !VideoAspects.Contains(aspectRatio))
            throw new ArgumentException($"Proporcje wideo: {string.Join(", ", VideoAspects)}.");

        if (durationSeconds is { } duration) VideoDuration = duration;
        if (aspectRatio is { Length: > 0 }) VideoAspect = aspectRatio;
    }

    void IAppControl.SwitchTab(string tab) => ActiveTab = Array.IndexOf(TabNames, tab);

    void IAppControl.AddReference(string path)
    {
        References.Add(new ReferenceItem(ImageOrientation.EnsureUpright(path), Path.GetFileName(path), path));
        if (SourceImage is null) LoadPrimary();
        Status = Loc.T("ref.msg.count", References.Count);
    }

    void IAppControl.ClearReferences() => ClearReferences();

    async Task<string> IAppControl.GenerateAsync(string kind)
    {
        var command = kind switch
        {
            "generate" => GenerateCommand,
            "edit" => EditCommand,
            _ => GenerateVideoCommand,
        };

        if (IsBusy) throw new InvalidOperationException("Aplikacja jest zajęta innym zadaniem.");
        if (!command.CanExecute(null))
            throw new InvalidOperationException(kind switch
            {
                "generate" => "Generowanie wymaga promptu i wybranego modelu.",
                "edit" => "Edycja wymaga promptu, wybranego modelu i co najmniej jednej referencji.",
                _ => "Wideo wymaga promptu i modelu wideo (z obsługą obrazu, jeśli są załączniki).",
            });

        var before = Gallery.FirstOrDefault()?.Id;
        await command.ExecuteAsync(null);

        // Błąd generowania nie rzuca wyjątku (trafia do statusu), więc brak nowego wpisu oznacza porażkę
        return Gallery.FirstOrDefault() is { } entry && entry.Id != before
            ? $"Gotowe: {entry.FilePath} (model {entry.Model}, koszt {entry.CostLabel})"
            : throw new InvalidOperationException($"Nie udało się: {Status}");
    }

    async Task IAppControl.AddReferenceFromUrlAsync(string url)
    {
        string path;
        using (Activity.Begin(Loc.T("activity.downloadImage")))
            path = await _downloads.DownloadAsync(url.Trim());
        References.Add(new ReferenceItem(path, ReferenceItem.Shorten(url), url));
        if (SourceImage is null) LoadPrimary();
        Status = Loc.T("ref.msg.addedUrl");
    }

    void IAppControl.RemoveReference(int index)
    {
        if (index < 0 || index >= References.Count)
            throw new ArgumentException($"Nie ma referencji o indeksie {index} (jest {References.Count}).");
        RemoveReference(References[index]);
    }

    void IAppControl.AddVideoAttachment(string path) =>
        VideoAttachments.Add(new ReferenceItem(ImageOrientation.EnsureUpright(path), Path.GetFileName(path), path));

    void IAppControl.ClearVideoAttachments() => VideoAttachments.Clear();

    void IAppControl.SetAspect(string ratio)
    {
        if (ratio == "source")
        {
            if (SourceImage is null) throw new InvalidOperationException("Brak referencji, z której można wziąć proporcje.");
            ApplySourceAspect();
            return;
        }

        if (!AspectPresets.Contains(ratio)) throw new ArgumentException($"Proporcje: {string.Join(", ", AspectPresets)} albo source.");
        SetAspect(ratio);
    }

    Task IAppControl.ReloadModelsAsync() => InitializeAsync();

    // Biblioteka dla StudioApi: te same kolekcje co w oknie, więc wyniki narzędzi od razu widać w galeriach
    IReadOnlyList<GalleryEntry> IStudioLibrary.Media => Gallery;
    IReadOnlyList<GalleryEntry> IStudioLibrary.Voice => Voice.Entries;

    void IStudioLibrary.AddMedia(GalleryEntry entry)
    {
        entry.Thumbnail = GalleryStore.Thumbnail(entry.FilePath);
        AddToGallery(entry);
    }

    void IStudioLibrary.AddVoice(GalleryEntry entry) => Voice.AddEntry(entry);

    bool IStudioLibrary.Remove(string id)
    {
        if (Gallery.FirstOrDefault(e => e.Id == id) is { } media)
        {
            RemoveFromGallery(media);
            return true;
        }

        return Voice.RemoveEntry(id);
    }

    void IStudioLibrary.Clear(bool voice)
    {
        if (voice) Voice.ClearEntries();
        else ClearGallery();
    }

    // Wybory okna jako domyślne ustawienia dla StudioApi: zmiana modelu przez narzędzie jest zmianą zaznaczenia w oknie,
    // a okno i tak zapisuje ją do state.json (SaveState przy każdej zmianie wyboru)
    private sealed class WindowDefaults(MainViewModel vm) : IStudioDefaults
    {
        public string ProviderId => vm._provider.Id;

        public string? Model(string kind) => kind switch
        {
            "generate" => vm.SelectedModel?.Id,
            "edit" => vm.SelectedEditModel?.Id,
            "video" => vm.SelectedVideoModel?.Id,
            "speech" => vm.Voice.SelectedSpeechModel?.Id,
            "transcription" => vm.Voice.SelectedTranscriptionModel?.Id,
            "chat" => vm.SelectedEnhancerModel,
            _ => null,
        };

        public void SetModel(string kind, string id)
        {
            switch (kind)
            {
                case "generate": vm.SelectedModel = vm.Models.First(m => m.Id == id); break;
                case "edit": vm.SelectedEditModel = vm.EditModels.First(m => m.Id == id); break;
                case "video": vm.SelectedVideoModel = vm.VideoModels.First(m => m.Id == id); break;
                case "speech": vm.Voice.SelectedSpeechModel = vm.Voice.SpeechModels.First(m => m.Id == id); break;
                case "transcription": vm.Voice.SelectedTranscriptionModel = vm.Voice.TranscriptionModels.First(m => m.Id == id); break;
                default: vm.SelectedEnhancerModel = id; break;
            }

            vm.SaveState();
        }
    }

    private ObservableCollection<ModelOption> ModelsFor(string kind) => kind switch
    {
        "generate" => Models,
        "edit" => EditModels,
        "video" => VideoModels,
        _ => throw new ArgumentException($"Nieznany rodzaj: {kind}."),
    };
}
