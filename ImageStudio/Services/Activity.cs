using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageStudio.Services;

// Rejestr trwających operacji sieciowych: stopka pokazuje spinner i opis, okno odpowiedzi jest przyciemnione.
// Begin zwraca token, którego Dispose kończy operację (using)
public interface IActivityTracker : System.ComponentModel.INotifyPropertyChanged
{
    bool IsActive { get; }

    // Opis ostatnio rozpoczętej operacji, z liczbą pozostałych równolegle ("Generowanie obrazu (+1)")
    string Label { get; }

    IDisposable Begin(string label);
}

public sealed class ActivityTracker : ObservableObject, IActivityTracker
{
    private readonly object _gate = new();
    private readonly List<string> _active = [];
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public bool IsActive
    {
        get { lock (_gate) return _active.Count > 0; }
    }

    public string Label
    {
        get
        {
            lock (_gate)
                return _active.Count switch
                {
                    0 => "",
                    1 => _active[0],
                    var n => $"{_active[^1]} (+{n - 1})",
                };
        }
    }

    public IDisposable Begin(string label)
    {
        lock (_gate) _active.Add(label);
        Notify();
        return new Lease(this, label);
    }

    private void End(string label)
    {
        lock (_gate) _active.Remove(label);
        Notify();
    }

    // Operacje kończą się też na wątkach tła (serwer MCP), a widok wiąże się z tymi właściwościami: powiadomienie wraca na wątek UI
    private void Notify()
    {
        void Raise()
        {
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(Label));
        }

        if (_ui is null || SynchronizationContext.Current == _ui) Raise();
        else _ui.Post(_ => Raise(), null);
    }

    private sealed class Lease(ActivityTracker tracker, string label) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) tracker.End(label);
        }
    }
}
