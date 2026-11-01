using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageStudio.Services;

// Rejestr trwających operacji sieciowych: stopka pokazuje spinner i opis, okno odpowiedzi jest przyciemnione.
// Begin zwraca token, którego Dispose kończy operację (using)
public interface IActivityTracker : System.ComponentModel.INotifyPropertyChanged
{
    bool IsActive { get; }

    // Opis ostatnio rozpoczętej operacji, z liczbą pozostałych równolegle ("Generowanie obrazu (+1)")
    string Label { get; }

    // Czy któraś trwająca operacja przyciemnia okno odpowiedzi (odpowiedź strumieniowa nie: tekst ma być czytelny)
    bool DimsResponse { get; }

    // dimResponse = false: operacja widoczna w stopce, ale bez przyciemniania okna odpowiedzi
    IDisposable Begin(string label, bool dimResponse = true);
}

public sealed class ActivityTracker : ObservableObject, IActivityTracker
{
    private readonly object _gate = new();
    private readonly List<(string Label, bool Dim)> _active = [];
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public bool IsActive
    {
        get { lock (_gate) return _active.Count > 0; }
    }

    public bool DimsResponse
    {
        get { lock (_gate) return _active.Any(a => a.Dim); }
    }

    public string Label
    {
        get
        {
            lock (_gate)
                return _active.Count switch
                {
                    0 => "",
                    1 => _active[0].Label,
                    var n => $"{_active[^1].Label} (+{n - 1})",
                };
        }
    }

    public IDisposable Begin(string label, bool dimResponse = true)
    {
        var entry = (label, dimResponse);
        lock (_gate) _active.Add(entry);
        Notify();
        return new Lease(this, entry);
    }

    private void End((string Label, bool Dim) entry)
    {
        lock (_gate) _active.Remove(entry);
        Notify();
    }

    // Operacje kończą się też na wątkach tła (serwer MCP), a widok wiąże się z tymi właściwościami: powiadomienie wraca na wątek UI
    private void Notify()
    {
        void Raise()
        {
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(DimsResponse));
        }

        if (_ui is null || SynchronizationContext.Current == _ui) Raise();
        else _ui.Post(_ => Raise(), null);
    }

    private sealed class Lease(ActivityTracker tracker, (string Label, bool Dim) entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) tracker.End(entry);
        }
    }
}
