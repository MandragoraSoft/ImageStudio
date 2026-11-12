using ImageStudio.Localization;
using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ImageStudio.Configuration;
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

    private static readonly ILogger Log = AppLog.For<ChatViewModel>();

    private readonly Func<IAiProvider> _provider;
    private readonly Action<string, bool> _report;
    private readonly Func<ToolCall, bool>? _confirm;
    private readonly ChatImageSupport? _images;
    private readonly Func<string, ChatModelInfo?>? _modelInfo;
    private readonly IActivityTracker? _activity;
    private readonly Func<bool>? _useStreaming;
    private readonly IChatSessionStore? _sessionStore;
    private readonly Func<string>? _additionalSystemContext;
    private readonly Func<IReadOnlyList<string>>? _analysisImagePaths;
    private CancellationTokenSource? _cts;

    // Treść ostatniej wiadomości zmieniła się w trakcie strumienia (widok przewija rozmowę na dół)
    public event Action? MessageUpdated;

    // Zmieniła się bieżąca sesja: id zapisanej rozmowy (pusty, gdy nowa rozmowa nie została jeszcze zapisana)
    public event Action<string>? SessionChanged;

    // Rozmowa z edytora promptu: przerwana lub nieudana odpowiedź nie zostaje w historii (rozmowa wraca do stanu sprzed wysłania)
    public bool DiscardIncompleteTurns { get; set; }

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];

    public ChatViewModel(Func<IAiProvider> provider, ObservableCollection<string> models,
                         Action<string, bool> report, string systemPrompt,
                         IToolHost? tools = null, Func<ToolCall, bool>? confirm = null,
                         ChatImageSupport? images = null, Func<string, ChatModelInfo?>? modelInfo = null,
                         bool reasoningEnabled = true, string reasoningEffort = ReasoningSupport.DefaultEffort,
                         IActivityTracker? activity = null, ObservableCollection<ModelOption>? modelOptions = null,
                         Func<bool>? useStreaming = null, IChatSessionStore? sessionStore = null,
                         Func<string>? additionalSystemContext = null,
                         Func<IReadOnlyList<string>>? analysisImagePaths = null)
    {
        _additionalSystemContext = additionalSystemContext;
        _analysisImagePaths = analysisImagePaths;
        _useStreaming = useStreaming;
        _sessionStore = sessionStore;
        LoadSessionList();
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
        if (tools is INotifyPropertyChanged observable)
            observable.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(ToolsWarning));
                RefreshContext();
            };
        Attachments.CollectionChanged += (_, _) =>
        {
            SendCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(ImageWarning));
        };
        Messages.CollectionChanged += (_, _) => RefreshContext();
    }

    // Wspólna z ulepszaniem promptu lista modeli czatu aktywnego dostawcy
    public ObservableCollection<string> Models { get; }

    // Te same modele z opisem (ikony możliwości); widok ma filtry ustawiane przez MainViewModel
    public ObservableCollection<ModelOption> ModelOptions { get; }
    public ICollectionView ModelOptionsView { get; }
    public ModelFilter? ConversationModelFilter { get; set; }

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

    // Zgody na narzędzia (na rozmowę i na stałe); domyślnie własne, aplikacja podaje te z panelu Narzędzia
    public ToolPermissions Permissions { get; init; } = new();

    // true = o zgodę pytamy w oknie czatu (PendingApproval); false = przez wywołanie zwrotne z konstruktora
    public bool InlineApproval { get; init; }

    // Prośba o zgodę czekająca na odpowiedź użytkownika (panel nad polem wpisywania); null = nic nie czeka
    [ObservableProperty]
    private ToolApproval? _pendingApproval;

    // Długie wyniki narzędzi: true = zwijaj bez pytania, null = pytaj (przy wyniku jest przycisk "Zwijaj zawsze")
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AskCollapse))]
    private bool? _collapseLong;

    public bool AskCollapse => CollapseLong is null;

    private void ApplyCollapse(ChatMessage message)
    {
        if (CollapseLong == true && message.IsLong) message.Collapsed = true;
    }

    [RelayCommand]
    private void ToggleCollapse(ChatMessage? message)
    {
        if (message is not null) message.Collapsed = !message.Collapsed;
    }

    [RelayCommand]
    private void CollapseAlways()
    {
        CollapseLong = true;
        foreach (var message in Messages) ApplyCollapse(message);
    }

    [RelayCommand]
    private void AnswerApproval(string choice)
    {
        if (PendingApproval is { } pending && Enum.TryParse<ApprovalChoice>(choice, out var parsed)) pending.Answer.TrySetResult(parsed);
    }

    // Źródło narzędzi (panel MCP wiąże się z jego właściwościami); null = czat bez narzędzi
    public IToolHost? Tools { get; }

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    public string? LastCompletedReply { get; private set; }

    // Obrazy czekające na wysłanie z następną wiadomością
    public ObservableCollection<ChatImage> Attachments { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(AnalyzeImagesCommand))]
    [NotifyPropertyChangedFor(nameof(ImageWarning), nameof(ToolsWarning), nameof(SupportsTools), nameof(SupportsReasoning), nameof(SelectedModelOption))]
    private string? _selectedModel;

    partial void OnSelectedModelChanged(string? value) => RefreshContext();

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
    public void RefreshReasoningSupport()
    {
        OnPropertyChanged(nameof(SupportsReasoning));
        OnPropertyChanged(nameof(SupportsTools));
        RefreshLanguageTexts();
        SendCommand.NotifyCanExecuteChanged();
        AnalyzeImagesCommand.NotifyCanExecuteChanged();
        RefreshContext();
    }

    public void RefreshLanguageTexts()
    {
        OnPropertyChanged(nameof(ToolsWarning));
        OnPropertyChanged(nameof(ImageWarning));
    }

    // --- Wskaźnik zużycia kontekstu ---
    // Zajętość okna modelu: tokeny z ostatniej odpowiedzi (API podaje usage), a gdy jej nie ma, szacunek (oznaczony "~")

    public bool HasContext => Messages.Count > 0;

    private TokenUsage? LastUsage => Messages.LastOrDefault(m => m.Role == "assistant" && m.Usage?.Input is not null)?.Usage;

    public bool ContextEstimated => LastUsage is null;

    public int ContextUsed => LastUsage is { Input: { } input } usage ? input + (usage.Output ?? 0) : EstimateContext();

    // Składniki szacunku. Instrukcja systemowa bez notatki o załącznikach (ta zapisuje pliki, a wskaźnik odświeża się przy każdej zmianie)
    private (int Instructions, int Definitions, int History, int Results) EstimateParts() =>
        (TokenEstimator.Estimate(SystemPrompt) + TokenEstimator.Estimate(SupportsTools ? Tools?.SystemNote : null),
         SupportsTools && Tools is { } host ? host.Tools.Sum(t => TokenEstimator.Estimate(t)) : 0,
         Messages.Where(m => m.Role != "tool").Sum(m => TokenEstimator.Estimate(m.Content)),
         Messages.Where(m => m.Role == "tool").Sum(m => TokenEstimator.Estimate(m.Content)));

    private int EstimateContext()
    {
        var parts = EstimateParts();
        return parts.Instructions + parts.Definitions + parts.History + parts.Results;
    }

    // Rozbicie szacunku na składniki (tooltip wskaźnika); każdy z "~", bo to szacunek
    public string ContextBreakdown
    {
        get
        {
            var parts = EstimateParts();
            return string.Join("\n",
                Loc.T("chat.context.system", "~" + TokenEstimator.Short(parts.Instructions)),
                Loc.T("chat.context.tools", "~" + TokenEstimator.Short(parts.Definitions)),
                Loc.T("chat.context.history", "~" + TokenEstimator.Short(parts.History)),
                Loc.T("chat.context.toolResults", "~" + TokenEstimator.Short(parts.Results)));
        }
    }

    // Rozmiar okna z katalogu modelu; null = katalog nie podaje
    public int? ContextLimit => SelectedModel is { } model && _modelInfo?.Invoke(model) is { ContextLength: { } limit } ? limit : null;

    public bool HasContextLimit => ContextLimit is > 0;

    public double ContextPercent => ContextLimit is { } limit && limit > 0 ? 100.0 * ContextUsed / limit : 0;

    // Pasek nie przekracza 100%, tekst pokazuje prawdziwy procent
    public double ContextBarValue => Math.Min(100, ContextPercent);

    public string ContextLevel => ContextPercent >= 90 ? "critical" : ContextPercent >= 75 ? "warn" : "normal";

    public string ContextText
    {
        get
        {
            var used = (ContextEstimated ? "~" : "") + TokenEstimator.Short(ContextUsed);
            return HasContextLimit
                ? $"{Math.Round(ContextPercent):0}% · {used} / {TokenEstimator.Short(ContextLimit!.Value)}"
                : Loc.T("chat.context.tokens", used);
        }
    }

    public void RefreshContext()
    {
        OnPropertyChanged(nameof(HasContext));
        OnPropertyChanged(nameof(ContextEstimated));
        OnPropertyChanged(nameof(ContextUsed));
        OnPropertyChanged(nameof(ContextLimit));
        OnPropertyChanged(nameof(HasContextLimit));
        OnPropertyChanged(nameof(ContextPercent));
        OnPropertyChanged(nameof(ContextBarValue));
        OnPropertyChanged(nameof(ContextLevel));
        OnPropertyChanged(nameof(ContextText));
        OnPropertyChanged(nameof(ContextBreakdown));
    }

    private ChatOptions? BuildOptions() =>
        ReasoningParameter is { } parameter ? new ChatOptions(parameter, ReasoningEnabled, SelectedReasoningEffort) : null;

    [ObservableProperty]
    private string _systemPrompt;

    partial void OnSystemPromptChanged(string value) => RefreshContext();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(NewChatCommand), nameof(NewIncognitoChatCommand), nameof(AttachImagesCommand),
                                nameof(PasteImageCommand), nameof(StopCommand), nameof(DeleteSessionCommand), nameof(AnalyzeImagesCommand))]
    [NotifyPropertyChangedFor(nameof(CanChangeSession))]
    private bool _isSending;

    // Ostrzeżenie, gdy dołączono obraz, a katalog mówi, że wybrany model obrazów nie przyjmuje
    public string ImageWarning =>
        Attachments.Count > 0 && SelectedModel is { } model && _images?.AcceptsImage(model) == false
            ? Loc.T("chat.msg.noImages")
            : "";

    public bool SupportsTools => SelectedModel is { } model && _modelInfo?.Invoke(model)?.SupportsTools == true;

    public string ToolsWarning => SupportsTools ? "" :
        _modelInfo?.Invoke(SelectedModel ?? "")?.SupportsTools == false
            ? Loc.T("chat.msg.noTools")
            : Loc.T("chat.msg.unknownTools");

    // Krótka odpowiedź obiecująca działanie ("Pozwól, że to zrobię", "Let me check"): model, który nie umie wywoływać narzędzi, kończy na obietnicy
    private static readonly System.Text.RegularExpressions.Regex PromiseText = new(
        @"\b(pozwól|zaraz|teraz|let me|i will|i'll|i'm going to)\b.{0,80}\b(zrobi\w*|sprawdz\w*|wykona\w*|użyj\w*|wywoła\w*|do (this|that|it)|check|call|use|run)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Singleline);

    internal static bool PromisesAction(string text) => text.Length is > 0 and < 600 && PromiseText.IsMatch(text);

    private bool CanSend => !IsSending && (!string.IsNullOrWhiteSpace(Input) || Attachments.Count > 0) && SelectedModel is not null
                            && !(Attachments.Count > 0 && _images?.AcceptsImage(SelectedModel) == false);

    private bool CanAnalyzeImages => !IsSending && SelectedModel is not null && _images is not null && _analysisImagePaths is not null;

    [RelayCommand(CanExecute = nameof(CanAnalyzeImages))]
    private async Task AnalyzeImagesAsync()
    {
        if (!CanAnalyzeImages) return;
        if (_images!.AcceptsImage(SelectedModel!) == false)
        {
            _report(Loc.T("gen.err.noImages", SelectedModel!), true);
            return;
        }

        var paths = _analysisImagePaths!();
        if (paths.Count == 0 && Attachments.Count == 0)
        {
            var selected = _images.Dialogs.PickImageFiles();
            if (selected is not { Length: > 0 }) return;
            paths = selected;
        }

        var images = new List<ChatImage>();
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        void Add(ChatImage image)
        {
            if (hashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.Bytes))))
                images.Add(image);
        }
        foreach (var path in paths)
        {
            try
            {
                var (bytes, mime) = ImageFitter.Downscale(File.ReadAllBytes(path), _images.MaxSide());
                Add(new ChatImage(bytes, mime));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or InvalidOperationException)
            {
                Log.LogWarning(ex, "Nie udało się wczytać obrazu do analizy: {Path}", path);
                _report(Loc.T("msg.loadNamedImageFailed", Path.GetFileName(path), ex.Message), true);
                return;
            }
        }
        foreach (var image in Attachments) Add(image);

        await SendMessageAsync(Loc.T("prompt.chatAnalyzeQuestion"), images, fromInput: false);
    }

    // Wysyła całą historię (z instrukcją systemową na początku). Gdy model prosi o narzędzia, wykonuje je (po zgodzie)
    // i odsyła wyniki, aż model odpowie tekstem. Błąd bez żadnego wykonanego narzędzia przywraca wiadomość i obrazy do pól.
    [RelayCommand(CanExecute = nameof(CanSend))]
    private Task SendAsync()
    {
        if (!CanSend)
        {
            if (ImageWarning.Length > 0) _report(ImageWarning, true);
            return Task.CompletedTask;
        }
        return SendMessageAsync(Input.Trim(), Attachments.ToList(), fromInput: true);
    }

    private async Task SendMessageAsync(string question, List<ChatImage> images, bool fromInput)
    {
        LastCompletedReply = null;
        var sent = new ChatMessage("user", question, Images: images.Count > 0 ? images : null);
        var start = Messages.Count;
        Messages.Add(sent);
        if (fromInput)
        {
            Input = "";
            Attachments.Clear();
        }
        IsSending = true;
        _report(Loc.T("chat.msg.answering"), false);
        ChatMessage? live = null;
        string? announced = null;
        var emptyReply = false;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var provider = _provider();
        var model = SelectedModel!;
        var supportsTools = SupportsTools;
        var options = (BuildOptions() ?? new ChatOptions("", false)) with { CacheSessionId = _sessionId };
        try
        {
            var definitions = fromInput && supportsTools ? Tools?.Tools ?? [] : [];
            Log.LogInformation("Czat: wysyłam do {Provider}/{Model} (historia {History}, obrazów {Images}, narzędzi {Tools}, strumień {Stream})",
                provider.Id, SelectedModel, Messages.Count, images.Count, definitions.Count, _useStreaming?.Invoke() == true);
            for (var round = 0; ; round++)
            {
                var history = BuildHistory(supportsTools && fromInput);
                var tools = definitions.Count > 0 ? definitions : null;
                ChatReply reply;
                if (_useStreaming?.Invoke() == true)
                {
                    // Pusta wiadomość pojawia się od razu i rośnie z każdym kawałkiem; historia powstała przed jej dodaniem
                    var message = live = new ChatMessage("assistant", "");
                    Messages.Add(message);
                    reply = await provider.ChatStreamAsync(model, history, tools, delta =>
                    {
                        if (delta.Content is { Length: > 0 }) message.Content += delta.Content;
                        if (delta.Reasoning is { Length: > 0 }) message.Reasoning = (message.Reasoning ?? "") + delta.Reasoning;
                        MessageUpdated?.Invoke();
                    }, ct, options);
                }
                else
                {
                    live = null;
                    reply = await provider.ChatAsync(model, history, tools, ct, options);
                }
                ct.ThrowIfCancellationRequested();
                if (!fromInput && reply.ToolCalls.Count > 0)
                    throw new InvalidOperationException(Loc.T("prompt.chatAnalyzeToolError"));
                if ((!supportsTools || _modelInfo?.Invoke(model)?.SupportsTools != true) && reply.ToolCalls.Count > 0)
                {
                    if (live is not null) { Messages.Remove(live); live = null; }
                    throw new InvalidOperationException(Loc.T("chat.msg.unexpectedTools"));
                }

                Log.LogInformation("Czat: odpowiedź rundy {Round}: tekst {Chars} zn., rozumowanie {Reasoning} zn., wywołania narzędzi [{Calls}], tokeny {Usage}",
                    round, reply.Content.Length, reply.Reasoning?.Length ?? 0, string.Join(", ", reply.ToolCalls.Select(c => c.Name)), reply.Usage);

                if (reply.ToolCalls.Count == 0)
                {
                    // Pusta odpowiedź (model nie napisał nic po wynikach narzędzi): nie zostawiamy pustej bańki w rozmowie
                    if (string.IsNullOrWhiteSpace(reply.Content) && (!fromInput || string.IsNullOrWhiteSpace(reply.Reasoning)))
                    {
                        if (live is not null) Messages.Remove(live);
                        if (!fromInput) Messages.Remove(sent);
                        emptyReply = true;
                        break;
                    }

                    Complete(live, new ChatMessage("assistant", reply.Content), reply);
                    LastCompletedReply = reply.Content;
                    announced = tools is null ? null
                        : tools.FirstOrDefault(t => reply.Content.Contains(t.Name, StringComparison.Ordinal))?.Name
                          ?? (PromisesAction(reply.Content) ? "" : null);
                    break;
                }

                // Wywołania ponad limit odrzucamy w całości: wiadomość z tool_calls bez wyników zepsułaby historię
                if (round >= MaxToolRounds)
                {
                    Log.LogWarning("Czat: przekroczono limit {Max} rund narzędzi, odrzucam wywołania", MaxToolRounds);
                    if (live is not null) Messages.Remove(live);
                    Messages.Add(new ChatMessage("assistant", Loc.T("chat.msg.toolLoop")));
                    break;
                }

                Complete(live, new ChatMessage("assistant", reply.Content, reply.ToolCalls), reply);
                var supportLost = false;
                foreach (var call in reply.ToolCalls)
                {
                    var canRun = _modelInfo?.Invoke(model)?.SupportsTools == true;
                    supportLost |= !canRun;
                    var output = canRun ? await RunToolAsync(call, model, ct) : Loc.T("chat.msg.unexpectedTools");
                    supportLost |= _modelInfo?.Invoke(model)?.SupportsTools != true;
                    var result = new ChatMessage("tool", output, ToolCallId: call.Id, ToolName: call.Name, Images: ToolResultImages(output));
                    ApplyCollapse(result);
                    Messages.Add(result);
                }
                if (supportLost) throw new InvalidOperationException(Loc.T("chat.msg.unexpectedTools"));
                _report(Loc.T("chat.msg.answering"), false);
            }

            if (emptyReply)
            {
                Log.LogWarning("Czat: {Model} zwrócił pustą odpowiedź (rund: {Rounds})", SelectedModel, Messages.Count(m => m.Role == "tool"));
                _report(Loc.T("chat.msg.emptyReply"), true);
            }
            // Model napisał, że wywoła narzędzie, ale nie wysłał wywołania (częste przy modelach bez obsługi narzędzi)
            else if (announced is not null)
            {
                Log.LogWarning("Czat: {Model} zapowiada działanie ({Tool}), ale nie wywołał narzędzia", SelectedModel, announced.Length > 0 ? announced : "bez nazwy");
                _report(announced.Length > 0 ? Loc.T("chat.msg.toolNotCalled", announced) : Loc.T("chat.msg.toolPromised", Tools?.Tools.Count ?? 0), true);
            }
            else
            {
                _report(Loc.T("msg.ready"), false);
            }
        }
        catch (Exception ex)
        {
            var stopped = ex is OperationCanceledException;
            if (stopped) Log.LogInformation("Czat: przerwano przez użytkownika ({Provider}/{Model})", provider.Id, SelectedModel);
            else Log.LogError(ex, "Czat: błąd przy {Provider}/{Model} (wiadomości w rozmowie {Count})", provider.Id, SelectedModel, Messages.Count);

            if (!fromInput || DiscardIncompleteTurns)
            {
                // Narzędzia, które już się wykonały, zostają w historii: ich skutki nastąpiły, więc ponowienie nie może ich powtórzyć
                var executed = fromInput && Messages.Skip(start).Any(m => m.Role == "tool" || m.HasToolCalls);
                if (executed)
                {
                    if (live is not null && !live.HasToolCalls) Messages.Remove(live);
                    CloseOpenToolCalls(start);
                }
                else
                {
                    while (Messages.Count > start) Messages.RemoveAt(Messages.Count - 1);
                    // Draft wraca do pól, jeśli użytkownik nie zdążył nic nowego wpisać
                    if (fromInput && Input.Length == 0)
                    {
                        Input = question;
                        if (Attachments.Count == 0) foreach (var image in images) Attachments.Add(image);
                    }
                }
            }
            else
            {
                // Po wykonanych narzędziach zostawiamy rozmowę, żeby ponowienie nie uruchomiło ich drugi raz
                if (live is not null && live.Content.Length == 0 && !live.HasToolCalls) Messages.Remove(live);
                if (Messages.Count == start + 1)
                {
                    Messages.Remove(sent);
                    Input = question;
                    foreach (var image in images) Attachments.Add(image);
                }
            }

            // Stop nie jest błędem; niepełny opis analizy nie zostaje w kontekście
            if (stopped) _report(Loc.T("chat.msg.stopped"), false);
            else _report(Loc.T("chat.msg.error", ex.Message), true);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsSending = false;
            SaveSession();
        }
    }

    // Przerywa trwającą odpowiedź (żądanie HTTP i narzędzia); tekst, który już napłynął, zostaje
    [RelayCommand(CanExecute = nameof(IsSending))]
    private void Stop() => _cts?.Cancel();

    // Wywołania przerwane przed wynikiem dostają jawny wynik „przerwano”, żeby historia była kompletna dla modelu
    private void CloseOpenToolCalls(int start)
    {
        for (var i = start; i < Messages.Count; i++)
        {
            foreach (var call in Messages[i].ToolCalls ?? [])
            {
                if (Messages.Any(m => m.Role == "tool" && m.ToolCallId == call.Id)) continue;
                var result = new ChatMessage("tool", Loc.T("chat.msg.toolStopped"), ToolCallId: call.Id, ToolName: call.Name);
                ApplyCollapse(result);
                Messages.Add(result);
            }
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
        RefreshContext();
        MessageUpdated?.Invoke();
    }

    // Narzędzia czytają pliki z dysku, więc obrazy załączone w rozmowie zapisujemy w folderze refs i podajemy modelowi ich ścieżki
    // (bez tego model zgaduje ścieżki typu /mnt/data/image.png). Nazwa pliku to skrót zawartości: ten sam obraz = ten sam plik.
    private string? AttachmentNote()
    {
        if (Tools is not { Tools.Count: > 0 }) return null;

        var paths = new List<string>();
        foreach (var image in Messages.Where(m => m.Role == "user").SelectMany(m => m.Images ?? []))
        {
            try
            {
                paths.Add(SaveForTools(image));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.LogWarning("Czat: nie zapisano obrazu dla narzędzi: {Message}", ex.Message);
            }
        }

        return paths.Count == 0
            ? null
            : "Obrazy załączone przez użytkownika są zapisane na dysku Windows. Gdy narzędzie potrzebuje pliku obrazu (referencje, załączniki), "
              + "użyj dokładnie tych ścieżek i nie wymyślaj własnych:\n" + string.Join("\n", paths.Select((p, i) => $"{i + 1}. {p}"));
    }

    private static string SaveForTools(ChatImage image)
    {
        var extension = image.Mime switch { "image/jpeg" => ".jpg", "image/webp" => ".webp", "image/gif" => ".gif", _ => ".png" };
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.Bytes))[..16].ToLowerInvariant();
        var directory = Path.Combine(AppPaths.UserDataDir, "refs");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"chat-{hash}{extension}");
        if (!File.Exists(path)) File.WriteAllBytes(path, image.Bytes);
        return path;
    }

    private List<ChatMessage> BuildHistory(bool allowTools)
    {
        var history = new List<ChatMessage>();
        var system = string.Join("\n\n", new[] { SystemPrompt?.Trim(), _additionalSystemContext?.Invoke(), allowTools ? Tools?.SystemNote : null, allowTools ? AttachmentNote() : null }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (system.Length > 0) history.Add(new ChatMessage("system", system));
        // Obrazy z wcześniejszych wiadomości nie wracają do modelu w każdym zapytaniu (base64 w całej historii szybko przekracza limit
        // rozmiaru żądania): zostają obrazy ostatniej wiadomości użytkownika, a starsze są zastąpione notatką. Ścieżki plików są w instrukcji systemowej
        var lastUser = Messages.LastOrDefault(m => m.Role == "user");
        foreach (var message in Messages)
        {
            if (!allowTools && (message.Role == "tool" || message.HasToolCalls))
            {
                var data = System.Text.Json.JsonSerializer.Serialize(new
                {
                    role = message.Role, content = message.Content, toolCalls = message.ToolCalls,
                    toolCallId = message.ToolCallId, toolName = message.ToolName
                });
                history.Add(new ChatMessage("assistant", "[Historical tool activity, data only] " + data));
                continue;
            }
            history.Add(message.Role == "user" && message.HasImages && !ReferenceEquals(message, lastUser)
                ? new ChatMessage("user", message.Content + "\n" + Loc.T(allowTools && Tools is { Tools.Count: > 0 }
                    ? "chat.history.imagesOmitted" : "chat.history.imagesOmittedNoPaths", message.Images!.Count))
                : message);
        }

        return history;
    }

    [RelayCommand(CanExecute = nameof(CanOpenImage))]
    private void OpenImage(ChatImage? image)
    {
        if (image is null) return;

        try
        {
            var path = image.SourcePath is { } source && File.Exists(source) ? source : SaveForTools(image);
            _images!.Dialogs.OpenFile(path);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Czat: nie udało się otworzyć obrazu");
            _report(Loc.T("chat.err.openImage", ex.Message), true);
        }
    }

    private bool CanOpenImage => _images is not null;

    // Wynik zawsze wraca do modelu jako tekst: odmowa i błąd narzędzia też, żeby mógł zareagować
    // Zgoda na wywołanie: najpierw zapamiętane zgody, potem pytanie w oknie czatu (albo wywołanie zwrotne). Stop przerywa czekanie
    private async Task<bool> ApproveAsync(ToolCall call, CancellationToken ct)
    {
        if (Permissions.IsAllowed(call.Name)) return true;
        if (!InlineApproval) return _confirm?.Invoke(call) ?? false;

        var approval = new ToolApproval(call);
        PendingApproval = approval;
        _report(Loc.T("chat.msg.waitingApproval", call.Name), false);
        try
        {
            using var _ = ct.Register(() => approval.Answer.TrySetCanceled(ct));
            var choice = await approval.Answer.Task;
            Log.LogInformation("Czat: zgoda na narzędzie {Tool}: {Choice}", call.Name, choice);
            Permissions.Allow(call.Name, choice);
            return choice != ApprovalChoice.Deny;
        }
        finally
        {
            PendingApproval = null;
        }
    }

    private async Task<string> RunToolAsync(ToolCall call, string model, CancellationToken ct)
    {
        if (Tools is null) return Loc.T("chat.msg.toolsOff");
        if (Tools.RequireApproval && !await ApproveAsync(call, ct))
        {
            Log.LogInformation("Czat: użytkownik odrzucił narzędzie {Tool} (argumenty: {Args})", call.Name, AppLog.Preview(call.ArgumentsJson));
            return Loc.T("chat.msg.toolRejected");
        }
        if (_modelInfo?.Invoke(model)?.SupportsTools != true) return Loc.T("chat.msg.unexpectedTools");

        _report(Loc.T("chat.msg.tool", call.Name), false);
        Log.LogInformation("Czat: wywołanie narzędzia {Tool} (argumenty: {Args})", call.Name, AppLog.Preview(call.ArgumentsJson));
        try
        {
            // Wywołanie narzędzia (serwer MCP) to też operacja sieciowa: spinner w stopce
            using var _ = _activity?.Begin(Loc.T("chat.msg.toolActivity", call.Name));
            var result = await Tools.CallAsync(call, ct);
            Log.LogInformation("Czat: wyniki narzędzia {Tool} ({Chars} zn.): {Result}", call.Name, result.Length, AppLog.Preview(result));
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Czat: narzędzie {Tool} zgłosiło błąd (argumenty: {Args})", call.Name, AppLog.Preview(call.ArgumentsJson));
            return Loc.T("chat.msg.toolError", ex.Message);
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
                _report(Loc.T("msg.noClipboardImage"), false);
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
            _report(Loc.T("msg.pasteFailed", ex.Message), true);
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
                _report(Loc.T("msg.loadFileFailed", Path.GetFileName(path), ex.Message), true);
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
            _report(Loc.T("msg.loadNamedImageFailed", name, ex.Message), true);
        }
    }

    // ---- Sesje ----
    // Każda rozmowa to sesja zapisywana po każdej odpowiedzi. Sesja incognito (nowa przyciskiem "Incognito") nie jest
    // zapisywana nigdzie i znika po zamknięciu albo przejściu do innej sesji.

    private string _sessionId = Guid.NewGuid().ToString("N");
    private DateTime _sessionCreated = DateTime.UtcNow;
    private string? _sessionTitle;
    private bool _switching;

    public ObservableCollection<ChatSessionInfo> Sessions { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSessionCommand))]
    private ChatSessionInfo? _selectedSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionLabel))]
    private bool _isIncognito;

    public bool CanChangeSession => !IsSending;

    public string SessionLabel => IsIncognito
        ? "Sesja incognito: rozmowa nie jest zapisywana"
        : _sessionTitle is { Length: > 0 } title ? title : "Nowa sesja";

    partial void OnSelectedSessionChanged(ChatSessionInfo? value)
    {
        if (!_switching && value is not null) OpenSession(value);
    }

    private void LoadSessionList()
    {
        Sessions.Clear();
        if (_sessionStore is null) return;
        foreach (var info in _sessionStore.List()) Sessions.Add(info);
    }

    private void StartSession(bool incognito)
    {
        _switching = true;
        try
        {
            Messages.Clear();
            LastCompletedReply = null;
            Attachments.Clear();
            Permissions.ClearSession();
            _sessionId = Guid.NewGuid().ToString("N");
            _sessionCreated = DateTime.UtcNow;
            _sessionTitle = null;
            IsIncognito = incognito;
            SelectedSession = null;
        }
        finally
        {
            _switching = false;
        }

        OnPropertyChanged(nameof(SessionLabel));
        // Pusty id: rozmowa nie ma jeszcze zapisanej sesji
        SessionChanged?.Invoke("");
    }

    // Wznawia zapisaną rozmowę (edytor promptu); false, gdy jej nie ma
    public bool ResumeSession(string id)
    {
        var data = _sessionStore?.Load(id);
        if (data is null) return false;

        Restore(data);
        // Ostatnia odpowiedź wraca, żeby można ją było znów zastosować
        var last = data.Messages.LastOrDefault();
        LastCompletedReply = last is { Role: "assistant", ToolCalls: not { Count: > 0 } } ? last.Content : null;
        return true;
    }

    private void OpenSession(ChatSessionInfo info)
    {
        var data = _sessionStore?.Load(info.Id);
        if (data is null)
        {
            // Plik zniknął lub jest uszkodzony: wpis znika z listy
            Sessions.Remove(info);
            return;
        }

        Restore(data);
    }

    private void Restore(ChatSessionData data)
    {
        _switching = true;
        try
        {
            Messages.Clear();
            LastCompletedReply = null;
            Attachments.Clear();
            Permissions.ClearSession();
            foreach (var message in data.Messages)
            {
                var restored = FromData(message);
                ApplyCollapse(restored);
                Messages.Add(restored);
            }

            _sessionId = data.Id;
            _sessionCreated = data.CreatedUtc;
            _sessionTitle = data.Title;
            IsIncognito = false;
            if (data.Model is { } model && Models.Contains(model)) SelectedModel = model;
        }
        finally
        {
            _switching = false;
        }

        OnPropertyChanged(nameof(SessionLabel));
        SessionChanged?.Invoke(data.Id);
        MessageUpdated?.Invoke();
    }

    // Zapis po każdej odpowiedzi; puste rozmowy i incognito nie są zapisywane
    private void SaveSession()
    {
        if (IsIncognito || _sessionStore is null || Messages.Count == 0) return;

        _sessionTitle ??= TitleFrom(Messages.FirstOrDefault(m => m.Role == "user")?.Content);
        var data = new ChatSessionData
        {
            Id = _sessionId,
            Title = _sessionTitle,
            CreatedUtc = _sessionCreated,
            UpdatedUtc = DateTime.UtcNow,
            Model = SelectedModel,
            Messages = Messages.Select(ToData).ToList(),
        };
        _sessionStore.Save(data);
        SessionChanged?.Invoke(data.Id);

        var info = new ChatSessionInfo(data.Id, data.Title, data.UpdatedUtc);
        _switching = true;
        try
        {
            var existing = Sessions.FirstOrDefault(s => s.Id == info.Id);
            if (existing is not null) Sessions.Remove(existing);
            Sessions.Insert(0, info);
            SelectedSession = info;
        }
        finally
        {
            _switching = false;
        }

        OnPropertyChanged(nameof(SessionLabel));
    }

    private static string TitleFrom(string? firstQuestion)
    {
        var text = string.IsNullOrWhiteSpace(firstQuestion) ? "Rozmowa z obrazem" : string.Join(' ', firstQuestion.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 48 ? text : text[..45] + "...";
    }

    // Wynik narzędzia z plikiem obrazu (pole Path w JSON, także w liście) pokazujemy w rozmowie jako obraz. Tylko do wyświetlenia:
    // do API idą obrazy użytkownika, a w sesji zapisujemy sam JSON (obraz wraca ze ścieżki)
    private static List<ChatImage>? ToolResultImages(string content)
    {
        var images = new List<ChatImage>();
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(content);
            var root = json.RootElement;
            var rows = root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("items", out var items) ? items : root;
            foreach (var row in rows.ValueKind == System.Text.Json.JsonValueKind.Array ? rows.EnumerateArray().ToArray() : [rows])
            {
                if (row.ValueKind != System.Text.Json.JsonValueKind.Object || !row.TryGetProperty("Path", out var path) || path.GetString() is not { } file) continue;
                var mime = Path.GetExtension(file).ToLowerInvariant() switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", _ => null };
                if (mime is not null && File.Exists(file)) images.Add(new ChatImage(File.ReadAllBytes(file), mime) { SourcePath = file });
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            // nie JSON albo plik nie do odczytu: wynik zostaje samym tekstem
        }

        return images.Count > 0 ? images : null;
    }

    private static ChatMessageData ToData(ChatMessage m) => new()
    {
        Role = m.Role,
        Content = m.Content,
        Reasoning = m.Reasoning,
        ToolCalls = m.ToolCalls?.ToList(),
        ToolCallId = m.ToolCallId,
        ToolName = m.ToolName,
        Usage = m.Usage,
        Images = m.Role == "tool" ? null : m.Images?.Select(i => new ChatImageData(i.Mime, Convert.ToBase64String(i.Bytes))).ToList(),
    };

    private static ChatMessage FromData(ChatMessageData d) => new(d.Role, d.Content, d.ToolCalls, d.ToolCallId, d.ToolName,
        d.Role == "tool" ? ToolResultImages(d.Content) : d.Images?.Select(i => new ChatImage(Convert.FromBase64String(i.Base64), i.Mime)).ToList(), d.Reasoning, d.Usage);

    // Nowa zapisywana sesja (poprzednia jest już zapisana po ostatniej odpowiedzi)
    [RelayCommand(CanExecute = nameof(CanNewChat))]
    private void NewChat() => StartSession(false);

    // Nowa sesja incognito: nic z niej nie trafia na dysk
    [RelayCommand(CanExecute = nameof(CanNewChat))]
    private void NewIncognitoChat() => StartSession(true);

    private bool CanNewChat => !IsSending;

    // Usuwa zaznaczoną sesję z dysku; jeśli to bieżąca rozmowa, zaczyna nową
    [RelayCommand(CanExecute = nameof(CanDeleteSession))]
    private void DeleteSession()
    {
        if (SelectedSession is not { } info) return;

        _sessionStore?.Delete(info.Id);
        Sessions.Remove(info);
        if (info.Id == _sessionId) StartSession(false);
        else SelectedSession = null;
    }

    private bool CanDeleteSession => !IsSending && SelectedSession is not null;
}
