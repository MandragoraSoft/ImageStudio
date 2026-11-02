namespace ImageStudio.Services;

// Odpowiedź użytkownika na prośbę o zgodę na narzędzie: jednorazowo, do końca rozmowy albo na stałe (zapisywane w state.json)
public enum ApprovalChoice { Deny, Once, Session, Always }

// Zgody na wywołania narzędzi (po nazwie widocznej dla modelu): na sesję (do nowej albo otwartej rozmowy) i na stałe.
// Zgoda nie obejmuje narzędzi wyłączonych na liście: te i tak nie trafiają do modelu
public sealed class ToolPermissions
{
    private readonly HashSet<string> _session = new(StringComparer.Ordinal);
    private readonly HashSet<string> _always = new(StringComparer.Ordinal);

    public event Action? Changed;

    public IReadOnlyCollection<string> Always => _always;
    public int SessionCount => _session.Count;

    // Wczytanie zapisanych zgód na stałe (bez zgłaszania zmiany, żeby nie zapisywać stanu od razu)
    public void Load(IEnumerable<string>? always)
    {
        foreach (var name in always ?? []) _always.Add(name);
    }

    public bool IsAllowed(string tool) => _always.Contains(tool) || _session.Contains(tool);

    public void Allow(string tool, ApprovalChoice choice)
    {
        var changed = choice switch
        {
            ApprovalChoice.Session => _session.Add(tool),
            ApprovalChoice.Always => _always.Add(tool),
            _ => false,
        };
        if (changed) Changed?.Invoke();
    }

    public void ClearSession()
    {
        if (_session.Count == 0) return;

        _session.Clear();
        Changed?.Invoke();
    }

    // Cofa wszystkie zgody (sesyjne i na stałe)
    public void Forget()
    {
        _session.Clear();
        _always.Clear();
        Changed?.Invoke();
    }
}

// Oczekująca prośba o zgodę pokazywana w oknie czatu: wywołanie i odpowiedź, na którą czeka pętla narzędzi
public sealed class ToolApproval(ToolCall call)
{
    private const int MaxArguments = 600;

    public ToolCall Call { get; } = call;

    public string Arguments => Call.ArgumentsJson.Length <= MaxArguments ? Call.ArgumentsJson : Call.ArgumentsJson[..MaxArguments] + "…";

    internal TaskCompletionSource<ApprovalChoice> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
