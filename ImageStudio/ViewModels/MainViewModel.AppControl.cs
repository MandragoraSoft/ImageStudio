using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using ImageStudio.Services;

namespace ImageStudio.ViewModels;

// Sterowanie aplikacją z zewnątrz (model w czacie i klienci MCP): te same operacje, które wykonuje użytkownik w oknie
public sealed partial class MainViewModel : IAppControl
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
        Status = $"Referencje: {References.Count}";
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

    IReadOnlyList<GalleryItem> IAppControl.ListGallery(int limit) =>
        Gallery.Take(limit)
            .Select(e => new GalleryItem(e.Kind, e.Model, e.Prompt, e.FilePath, e.CostLabel, e.CreatedUtc))
            .ToList();

    private ObservableCollection<ModelOption> ModelsFor(string kind) => kind switch
    {
        "generate" => Models,
        "edit" => EditModels,
        "video" => VideoModels,
        _ => throw new ArgumentException($"Nieznany rodzaj: {kind}."),
    };
}
