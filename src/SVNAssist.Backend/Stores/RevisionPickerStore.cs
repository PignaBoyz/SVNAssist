using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>
/// Scelta guidata della revisione per "Update to revision": elenco delle ultime revisioni (<c>svn log</c>),
/// "Carica altre" e conferma. Tiene solo l'elenco; l'update lo esegue <see cref="ChangesStore"/>.
/// </summary>
internal sealed class RevisionPickerStore
{
    internal const int PageSize = 30;

    private readonly ISvnService _svn;
    private readonly object _lock = new();
    private bool _open, _loading, _canMore;
    private string _error = string.Empty;
    private int _limit = PageSize;
    private IReadOnlyList<string> _targets = [];
    private IReadOnlyList<RevItemDto> _items = [];

    public RevisionPickerStore(ISvnService svn) => _svn = svn;

    public event Action? Changed;

    internal bool IsOpen { get { lock (_lock) return _open; } }

    /// <summary>Percorsi su cui eseguire l'update alla conferma.</summary>
    internal IReadOnlyList<string> Targets { get { lock (_lock) return _targets; } }

    internal RevPickerDto GetSnapshot()
    {
        lock (_lock)
            return new RevPickerDto(_open, _targets.FirstOrDefault() ?? string.Empty, _loading, _error, _canMore, _items);
    }

    /// <summary>Apre la scelta per i percorsi indicati e carica le ultime revisioni del primo.</summary>
    internal async Task OpenAsync(IReadOnlyList<string> targets, CancellationToken ct)
    {
        lock (_lock)
        {
            _open = true;
            _targets = targets;
            _limit = PageSize;
            _items = [];
            _canMore = false;
            _error = string.Empty;
        }

        await LoadAsync(ct);
    }

    /// <summary>"Carica altre": rilegge il log con una pagina in piu'.</summary>
    internal Task MoreAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_open || _loading)
                return Task.CompletedTask;
            _limit += PageSize;
        }

        return LoadAsync(ct);
    }

    internal void Close()
    {
        lock (_lock)
        {
            _open = false;
            _items = [];
            _error = string.Empty;
        }

        Changed?.Invoke();
    }

    /// <summary>Errore mostrato nella finestra (es. numero non valido); la finestra resta aperta.</summary>
    internal void SetError(string message)
    {
        lock (_lock)
            _error = message;
        Changed?.Invoke();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        string target;
        int limit;
        lock (_lock)
        {
            target = _targets.FirstOrDefault() ?? string.Empty;
            limit = _limit;
            _loading = true;
            _error = string.Empty;
        }

        Changed?.Invoke();
        try
        {
            var log = await _svn.GetLogAsync(target, limit, ct);
            lock (_lock)
            {
                _items = log.Select(e => new RevItemDto(e.Revision, e.Author, e.Date.ToString("o"), FirstLine(e.Message))).ToList();
                _canMore = log.Count >= limit;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            lock (_lock)
                _error = Loc.T("Impossibile leggere le revisioni: {0}", ex.Message);
        }
        finally
        {
            lock (_lock)
                _loading = false;
            Changed?.Invoke();
        }
    }

    private static string FirstLine(string message)
        => (message ?? string.Empty).Split('\n', 2)[0].Trim();
}
