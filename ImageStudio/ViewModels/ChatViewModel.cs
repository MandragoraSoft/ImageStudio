using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Services;

namespace ImageStudio.ViewModels;

// Klasyczny czat z modelem tekstowym: historia rozmowy żyje tylko w pamięci, wybór modelu i instrukcja systemowa są zapamiętywane
public sealed partial class ChatViewModel : ObservableObject
{
    private readonly Func<IAiProvider> _provider;
    private readonly Action<string, bool> _report;

    public ChatViewModel(Func<IAiProvider> provider, ObservableCollection<string> models,
                         Action<string, bool> report, string systemPrompt)
    {
        _provider = provider;
        Models = models;
        _report = report;
        _systemPrompt = systemPrompt;
    }

    // Wspólna z ulepszaniem promptu lista modeli czatu aktywnego dostawcy
    public ObservableCollection<string> Models { get; }

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

    // Wysyła całą historię (z instrukcją systemową na początku); przy błędzie wiadomość wraca do pola, żeby ponowić
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var question = Input.Trim();
        var sent = new ChatMessage("user", question);
        Messages.Add(sent);
        Input = "";
        IsSending = true;
        _report("Model odpowiada...", false);
        try
        {
            var history = new List<ChatMessage>();
            if (!string.IsNullOrWhiteSpace(SystemPrompt)) history.Add(new ChatMessage("system", SystemPrompt.Trim()));
            history.AddRange(Messages);

            var reply = await _provider().ChatAsync(SelectedModel!, history);
            Messages.Add(new ChatMessage("assistant", reply));
            _report("Gotowy", false);
        }
        catch (Exception ex)
        {
            Messages.Remove(sent);
            Input = question;
            _report($"Błąd czatu: {ex.Message}", true);
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanNewChat))]
    private void NewChat() => Messages.Clear();

    private bool CanNewChat => !IsSending;
}
