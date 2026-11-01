using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Services;

namespace ImageStudio.ViewModels;

// Klasyczny czat z modelem tekstowym i opcjonalnymi narzędziami (MCP): historia rozmowy żyje tylko w pamięci,
// wybór modelu i instrukcja systemowa są zapamiętywane
public sealed partial class ChatViewModel : ObservableObject
{
    // Tyle razy model może z rzędu poprosić o narzędzia w jednej odpowiedzi, zanim przerwiemy pętlę
    private const int MaxToolRounds = 8;

    private readonly Func<IAiProvider> _provider;
    private readonly Action<string, bool> _report;
    private readonly Func<ToolCall, bool>? _confirm;

    public ChatViewModel(Func<IAiProvider> provider, ObservableCollection<string> models,
                         Action<string, bool> report, string systemPrompt,
                         IToolHost? tools = null, Func<ToolCall, bool>? confirm = null)
    {
        _provider = provider;
        Models = models;
        _report = report;
        _systemPrompt = systemPrompt;
        Tools = tools;
        _confirm = confirm;
    }

    // Wspólna z ulepszaniem promptu lista modeli czatu aktywnego dostawcy
    public ObservableCollection<string> Models { get; }

    // Źródło narzędzi (panel MCP wiąże się z jego właściwościami); null = czat bez narzędzi
    public IToolHost? Tools { get; }

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string? _selectedModel;

    [ObservableProperty]
    private string _systemPrompt;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(NewChatCommand))]
    private bool _isSending;

    private bool CanSend => !IsSending && !string.IsNullOrWhiteSpace(Input) && SelectedModel is not null;

    // Wysyła całą historię (z instrukcją systemową na początku). Gdy model prosi o narzędzia, wykonuje je (po zgodzie)
    // i odsyła wyniki, aż model odpowie tekstem. Błąd bez żadnego wykonanego narzędzia przywraca wiadomość do pola.
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var question = Input.Trim();
        var sent = new ChatMessage("user", question);
        var start = Messages.Count;
        Messages.Add(sent);
        Input = "";
        IsSending = true;
        _report("Model odpowiada...", false);
        try
        {
            var definitions = Tools?.Tools ?? [];
            for (var round = 0; ; round++)
            {
                var reply = await _provider().ChatAsync(SelectedModel!, BuildHistory(), definitions.Count > 0 ? definitions : null);
                if (reply.ToolCalls.Count == 0)
                {
                    Messages.Add(new ChatMessage("assistant", reply.Content));
                    break;
                }

                // Wywołania ponad limit odrzucamy w całości: wiadomość z tool_calls bez wyników zepsułaby historię
                if (round >= MaxToolRounds)
                {
                    Messages.Add(new ChatMessage("assistant", "Przerwano: model zbyt wiele razy z rzędu prosił o narzędzia."));
                    break;
                }

                Messages.Add(new ChatMessage("assistant", reply.Content, reply.ToolCalls));
                foreach (var call in reply.ToolCalls)
                    Messages.Add(new ChatMessage("tool", await RunToolAsync(call), ToolCallId: call.Id, ToolName: call.Name));
                _report("Model odpowiada...", false);
            }

            _report("Gotowy", false);
        }
        catch (Exception ex)
        {
            // Po wykonanych narzędziach zostawiamy rozmowę, żeby ponowienie nie uruchomiło ich drugi raz
            if (Messages.Count == start + 1)
            {
                Messages.Remove(sent);
                Input = question;
            }

            _report($"Błąd czatu: {ex.Message}", true);
        }
        finally
        {
            IsSending = false;
        }
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
            return await Tools.CallAsync(call);
        }
        catch (Exception ex)
        {
            return $"Błąd narzędzia: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanNewChat))]
    private void NewChat() => Messages.Clear();

    private bool CanNewChat => !IsSending;
}
