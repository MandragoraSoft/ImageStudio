using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Services;

namespace ImageStudio.ViewModels;

public sealed class OutgoingRequestViewModel
{
    public OutgoingRequestInfo Info { get; }
    public DateTime StartedLocal => Info.StartedAt.LocalDateTime;
    public IRelayCommand CancelCommand { get; }

    public OutgoingRequestViewModel(OutgoingRequestInfo info, OutgoingRequestRegistry registry)
    {
        Info = info;
        CancelCommand = new RelayCommand(() => registry.Cancel(info.Id));
    }
}

public sealed class OutgoingRequestsViewModel : ObservableObject
{
    private readonly OutgoingRequestRegistry? _registry;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly ObservableCollection<OutgoingRequestViewModel> _requests = [];
    private bool _isOpen;

    public ReadOnlyObservableCollection<OutgoingRequestViewModel> Requests { get; }
    public bool HasRequests => _requests.Count != 0;
    public int Count => _requests.Count;
    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value && HasRequests);
    }

    public OutgoingRequestsViewModel(OutgoingRequestRegistry? registry)
    {
        _registry = registry;
        Requests = new(_requests);
        if (registry is not null)
        {
            registry.Changed += OnChanged;
            Refresh();
        }
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess()) Refresh();
        else if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(Refresh);
    }

    private void Refresh()
    {
        var active = _registry!.ActiveRequests;
        var ids = active.Select(request => request.Id).ToHashSet();
        for (var i = _requests.Count - 1; i >= 0; i--)
            if (!ids.Contains(_requests[i].Info.Id)) _requests.RemoveAt(i);
        var existing = _requests.Select(request => request.Info.Id).ToHashSet();
        foreach (var request in active)
            if (existing.Add(request.Id)) _requests.Add(new(request, _registry));
        if (!HasRequests) IsOpen = false;
        OnPropertyChanged(nameof(HasRequests));
        OnPropertyChanged(nameof(Count));
    }
}
