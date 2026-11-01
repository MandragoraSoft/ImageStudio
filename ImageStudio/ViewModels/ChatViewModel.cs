using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Services;

namespace ImageStudio.ViewModels;

// Co czat potrzebuje do obrazów: okna wyboru pliku i schowek, informacja, czy model przyjmuje obrazy (null = nie wiadomo),
// i limit dłuższego boku (większe obrazy są zmniejszane przed wysłaniem)
public sealed record ChatImageSupport(IDialogService Dialogs, Func<string, bool?> AcceptsImage, Func<int> MaxSide);

// Klasyczny czat z modelem tekstowym, obrazami (modele z wejściem obrazowym) i opcjonalnymi narzędziami (MCP):
// historia rozmowy żyje tylko w pamięci, wybór modelu i instrukcja systemowa są zapamiętywane
public sealed partial class ChatViewModel : ObservableObject
{
    // Tyle razy model może z rzędu poprosić o narzędzia w jednej odpowiedzi, zanim przerwiemy pętlę
    private const int MaxToolRounds = 8;
    private const int DefaultMaxSide = 1536;

    private readonly Func<IAiProvider> _provider;
    private readonly Action<string, bool> _report;
    private readonly Func<ToolCall, bool>? _confirm;
    private readonly ChatImageSupport? _images;
    private readonly Func<string, ChatModelInfo?>? _modelInfo;
    private readonly IActivityTracker? _activity;
    private readonly Func<bool>? _useStreaming;

    // Treść ostatniej wiadomości zmieniła się w trakcie strumienia (widok przewija rozmowę na dół)
    public event Action? MessageUpdated;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];

    public ChatViewModel(Func<IAiProvider> provider, ObservableCollection<string> models,
                         Action<string, bool> report, string systemPrompt,
                         IToolHost? tools = null, Func<ToolCall, bool>? confirm = null,
                         ChatImageSupport? images = null, Func<string, ChatModelInfo?>? modelInfo = null,
                         bool reasoningEnabled = true, string reasoningEffort = ReasoningSupport.DefaultEffort,
                         IActivityTracker? activity = null, ObservableCollection<ModelOption>? modelOptions = null,
                         Func<bool>? useStreaming = null)
    {
        _useStreaming = useStreaming;
        ModelOptions = modelOptions ?? [];
        ModelOptionsView = CollectionViewSource.GetDefaultView(ModelOptions);
        ModelOptions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SelectedModelOption));
        _activity = activity;
        _modelInfo = modelInfo;
        _reasoningEnabled = reasoningEnabled;
        _selectedReasoningEffort = reasoningEffort;
        _provider = provider;
        Models = models;
        _report = report;
        _systemPrompt = systemPrompt;
        Tools = tools;
        _confirm = confirm;
        _images = images;
        Attachments.CollectionChanged += (_, _) =>
        {
            SendCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(ImageWarning));
        };
    }

    // Wspólna z ulepszaniem promptu lista modeli czatu aktywnego dostawcy
    public ObservableCollection<string> Models { get; }

    // Te same modele z opisem (ikony możliwości); widok ma filtry ustawiane przez MainViewModel
    public ObservableCollection<ModelOption> ModelOptions { get; }
    public ICollectionView ModelOptionsView { get; }

    // Wybór w selektorze z opisem; null z selektora (filtr ukrył wybraną pozycję) nie zmienia wyboru
    public ModelOption? SelectedModelOption
    {
        get => ModelOptions.FirstOrDefault(o => o.Id == SelectedModel);
        set
        {
            if (value is not null) SelectedModel = value.Id;
            else OnPropertyChanged();
        }
    }

    // Źródło narzędzi (panel MCP wiąże się z jego właściwościami); null = czat bez narzędzi
    public IToolHost? Tools { get; }

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    // Obrazy czekające na wysłanie z następną wiadomością
    public ObservableCollection<ChatImage> Attachments { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(ImageWarning), nameof(SupportsReasoning), nameof(SelectedModelOption))]
    private string? _selectedModel;

    // Rozumowanie: sterowanie pojawia się tylko dla modeli, które je deklarują (supported_parameters z katalogu)
    public IReadOnlyList<string> ReasoningEfforts => ReasoningSupport.Levels;

    [ObservableProperty]
    private bool _reasoningEnabled;

    [ObservableProperty]
    private string _selectedReasoningEffort;

    private string? ReasoningParameter =>
        SelectedModel is { } model && _modelInfo?.Invoke(model) is { } info ? ReasoningSupport.ParameterFor(info.SupportedParameters) : null;

    public bool SupportsReasoning => ReasoningParameter is not null;

    // Katalog modeli mógł się właśnie wczytać: widoczność sterowania liczy się od nowa
    public void RefreshReasoningSupport() => OnPropertyChanged(nameof(SupportsReasoning));

    private ChatOptions? BuildOptions() =>
        ReasoningParameter is { } parameter ? new ChatOptions(parameter, ReasoningEnabled, SelectedReasoningEffort) : null;

    [ObservableProperty]
    private string _systemPrompt;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(NewChatCommand), nameof(AttachImagesCommand), nameof(PasteImageCommand))]
    private bool _isSending;

    // Ostrzeżenie, gdy dołączono obraz, a katalog mówi, że wybrany model obrazów nie przyjmuje
    public string ImageWarning =>
        Attachments.Count > 0 && SelectedModel is { } model && _images?.AcceptsImage(model) == false
            ? "Ten model nie przyjmuje obrazów: wiadomość może zostać odrzucona. Wybierz model z obsługą obrazu."
            : "";

    private bool CanSend => !IsSending && (!string.IsNullOrWhiteSpace(Input) || Attachments.Count > 0) && SelectedModel is not null;

    // Wysyła całą historię (z instrukcją systemową na początku). Gdy model prosi o narzędzia, wykonuje je (po zgodzie)
    // i odsyła wyniki, aż model odpowie tekstem. Błąd bez żadnego wykonanego narzędzia przywraca wiadomość i obrazy do pól.
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var question = Input.Trim();
        var images = Attachments.ToList();
        var sent = new ChatMessage("user", question, Images: images.Count > 0 ? images : null);
        var start = Messages.Count;
        Messages.Add(sent);
        Input = "";
        Attachments.Clear();
        IsSending = true;
        _report("Model odpowiada...", false);
        ChatMessage? live = null;
        try
        {
            var definitions = Tools?.Tools ?? [];
            for (var round = 0; ; round++)
            {
                var history = BuildHistory();
                var tools = definitions.Count > 0 ? definitions : null;
                ChatReply reply;
                if (_useStreaming?.Invoke() == true)
                {
                    // Pusta wiadomość pojawia się od razu i rośnie z każdym kawałkiem; historia powstała przed jej dodaniem
                    var message = live = new ChatMessage("assistant", "");
                    Messages.Add(message);
                    reply = await _provider().ChatStreamAsync(SelectedModel!, history, tools, delta =>
                    {
                        if (delta.Content is { Length: > 0 }) message.Content += delta.Content;
                        if (delta.Reasoning is { Length: > 0 }) message.Reasoning = (message.Reasoning ?? "") + delta.Reasoning;
                        MessageUpdated?.Invoke();
                    }, options: BuildOptions());
                }
                else
                {
                    live = null;
                    reply = await _provider().ChatAsync(SelectedModel!, history, tools, options: BuildOptions());
                }

                if (reply.ToolCalls.Count == 0)
                {
                    Complete(live, new ChatMessage("assistant", reply.Content), reply);
                    break;
                }

                // Wywołania ponad limit odrzucamy w całości: wiadomość z tool_calls bez wyników zepsułaby historię
                if (round >= MaxToolRounds)
                {
                    if (live is not null) Messages.Remove(live);
                    Messages.Add(new ChatMessage("assistant", "Przerwano: model zbyt wiele razy z rzędu prosił o narzędzia."));
                    break;
                }

                Complete(live, new ChatMessage("assistant", reply.Content, reply.ToolCalls), reply);
                foreach (var call in reply.ToolCalls)
                    Messages.Add(new ChatMessage("tool", await RunToolAsync(call), ToolCallId: call.Id, ToolName: call.Name));
                _report("Model odpowiada...", false);
            }

            _report("Gotowy", false);
        }
        catch (Exception ex)
        {
            // Pusta wiadomość ze strumienia, który nic nie dostarczył, nie zostaje w rozmowie
            if (live is not null && live.Content.Length == 0 && !live.HasToolCalls) Messages.Remove(live);

            // Po wykonanych narzędziach zostawiamy rozmowę, żeby ponowienie nie uruchomiło ich drugi raz
            if (Messages.Count == start + 1)
            {
                Messages.Remove(sent);
                Input = question;
                foreach (var image in images) Attachments.Add(image);
            }

            _report($"Błąd czatu: {ex.Message}", true);
        }
        finally
        {
            IsSending = false;
        }
    }

    // Zamyka odpowiedź: w trybie strumieniowym uzupełnia już widoczną wiadomość pełnymi danymi, inaczej dodaje nową
    private void Complete(ChatMessage? live, ChatMessage fresh, ChatReply reply)
    {
        var target = live ?? fresh;
        target.Content = reply.Content;
        target.Reasoning = reply.Reasoning;
        target.Usage = reply.Usage;
        if (fresh.HasToolCalls) target.ToolCalls = fresh.ToolCalls;
        if (live is null) Messages.Add(target);
        MessageUpdated?.Invoke();
    }

    private List<ChatMessage> BuildHistory()
    {
        var history = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(SystemPrompt)) history.Add(new ChatMessage("system", SystemPrompt.Trim()));
        history.AddRange(Messages);
        return history;
    }

    // Wynik zawsze wraca do modelu jako tekst: odmowa i błąd narzędzia też, żeby mógł zareagować
    private async Task<string> RunToolAsync(ToolCall call)
    {
        if (Tools is null) return "Narzędzia są wyłączone.";
        if (Tools.RequireApproval && !(_confirm?.Invoke(call) ?? false)) return "Użytkownik odrzucił wywołanie narzędzia.";

        _report($"Narzędzie {call.Name}...", false);
        try
        {
            // Wywołanie narzędzia (serwer MCP) to też operacja sieciowa: spinner w stopce
            using var _ = _activity?.Begin($"Narzędzie {call.Name}");
            return await Tools.CallAsync(call);
        }
        catch (Exception ex)
        {
            return $"Błąd narzędzia: {ex.Message}";
        }
    }

    // --- Obrazy ---

    private bool CanAttach => !IsSending && _images is not null;

    [RelayCommand(CanExecute = nameof(CanAttach))]
    private void AttachImages()
    {
        if (_images!.Dialogs.PickImageFiles() is { } files) AddFiles(files);
    }

    [RelayCommand(CanExecute = nameof(CanAttach))]
    private void PasteImage()
    {
        try
        {
            if (_images!.Dialogs.GetClipboardImage() is not { } image)
            {
                _report("Schowek nie zawiera obrazu.", false);
                return;
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            AddImage(stream.ToArray(), "schowka");
        }
        catch (Exception ex)
        {
            _report($"Nie udało się wkleić obrazu: {ex.Message}", true);
        }
    }

    [RelayCommand]
    private void RemoveAttachment(ChatImage? image)
    {
        if (image is not null) Attachments.Remove(image);
    }

    // Pliki z okna wyboru i z przeciągnięcia; inne typy plików są pomijane
    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(p => ImageExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())))
        {
            try
            {
                AddImage(File.ReadAllBytes(path), Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                _report($"Nie udało się wczytać {Path.GetFileName(path)}: {ex.Message}", true);
            }
        }
    }

    // Dekodowanie sprawdza, że to obraz; duże obrazy są zmniejszane do limitu dłuższego boku
    private void AddImage(byte[] bytes, string name)
    {
        try
        {
            var (fitted, mime) = ImageFitter.Downscale(bytes, _images?.MaxSide() ?? DefaultMaxSide);
            Attachments.Add(new ChatImage(fitted, mime));
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException)
        {
            _report($"Nie udało się wczytać obrazu {name}: {ex.Message}", true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanNewChat))]
    private void NewChat() => Messages.Clear();

    private bool CanNewChat => !IsSending;
}
