using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>
/// Vista SVN Log: cronologia delle revisioni e diff della revisione selezionata.
/// </summary>
/// <remarks>
/// Si caricano <see cref="PageSize"/> revisioni alla volta: "Carica altre 50" rilegge il log con un limite piu' alto
/// (<c>svn log -v --xml -l N</c>). La working copy e' quella rilevata dalla solution (<see cref="ApplyAutoDetectedWorkingCopyAsync"/>)
/// o, se valorizzata, quella delle impostazioni; l'utente puo' anche digitarne una (<c>logLoad</c>).
/// </remarks>
internal sealed class LogStore : ViewStore
{
    /// <summary>Revisioni caricate per pagina.</summary>
    internal const int PageSize = 50;

    private static readonly Regex RepositoryUrl = new(@"^(https?|svn|svn\+ssh|file)://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ISvnService _svnService;
    private readonly ISvnAssistSettings _settings;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private string _path = string.Empty;
    private string _msg = "Inserisci il percorso della working copy e premi Carica log.";
    private bool _err;
    private bool _loading;
    private int _limit;
    private bool _canMore;
    private long? _sel;
    private string _diff = string.Empty;
    private IReadOnlyList<LogEntryDto> _entries = [];
    private bool _initialized;
    private string? _solutionRoot;

    public LogStore(ISvnService svnService, ISvnAssistSettings settings)
    {
        _svnService = svnService;
        _settings = settings;
    }

    /// <summary>Percorso della working copy (o URL del repository) interrogata.</summary>
    internal string CurrentPath
    {
        get { lock (_lock) return _path; }
    }

    /// <inheritdoc />
    internal override object GetSnapshot()
    {
        lock (_lock)
            return new LogState(_path, _msg, _err, _loading, _canMore, _sel, _entries, _diff);
    }

    /// <summary>All'apertura della vista: se c'e' una working copy (solution o impostazioni) e non e' ancora stata caricata, parte da sola.</summary>
    internal override async Task OnReadyAsync(CancellationToken ct)
    {
        if (!_initialized)
        {
            _initialized = true;
            string? root;
            lock (_lock)
                root = _solutionRoot;
            await ApplyAutoDetectedWorkingCopyAsync(root, ct);
        }
    }

    /// <summary>La solution di Visual Studio e' cambiata (<c>null</c> = nessuna): se la vista e' aperta carica il log della nuova working copy.</summary>
    internal async Task OnSolutionChangedAsync(string? solutionDir, CancellationToken ct)
    {
        string? root = null;
        if (!string.IsNullOrWhiteSpace(solutionDir))
        {
            try
            {
                root = SvnService.FindSvnRoot(solutionDir);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                root = null;
            }
        }

        lock (_lock)
            _solutionRoot = root;
        if (_initialized)
            await ApplyAutoDetectedWorkingCopyAsync(root, ct);
    }

    internal override async Task<object?> DispatchAsync(string name, JsonElement args, CancellationToken ct)
    {
        switch (name)
        {
            case "logLoad":
                await LoadFromUserPathAsync(Args.Str(args, "path"), ct);
                break;
            case "logMore":
                await LoadAsync(Math.Max(_limit, PageSize) + PageSize, ct);
                break;
            case "logSelect":
                await SelectAsync(Args.Long(args, "rev"), ct);
                break;
            default:
                throw new ArgumentException($"Comando sconosciuto: {name}");
        }

        return null;
    }

    /// <summary>
    /// Imposta la working copy rilevata dalla solution e carica il log se e' cambiata
    /// (non azzera un percorso scritto a mano quando la solution non e' SVN).
    /// </summary>
    internal async Task ApplyAutoDetectedWorkingCopyAsync(string? svnRoot, CancellationToken ct)
    {
        // Il setting "workingCopyPath", se valorizzato, vince sull'auto-detect (come nella vista Changes).
        var configured = (await _settings.GetWorkingCopyPathAsync(ct)).Trim();
        if (configured.Length > 0)
            svnRoot = configured;

        if (string.IsNullOrWhiteSpace(svnRoot) || string.Equals(CurrentPath, svnRoot, StringComparison.OrdinalIgnoreCase))
            return;

        lock (_lock)
            _path = svnRoot;
        await LoadAsync(PageSize, ct);
    }

    /// <summary>
    /// Il percorso digitato dall'utente e' l'unico percorso che arriva dalla UI: deve essere un URL di repository
    /// oppure una cartella esistente dentro una working copy SVN.
    /// </summary>
    private async Task LoadFromUserPathAsync(string typed, CancellationToken ct)
    {
        var path = typed.Trim();
        if (path.Length == 0)
        {
            Set("Inserisci il percorso della working copy.", err: false);
            return;
        }

        if (!RepositoryUrl.IsMatch(path))
        {
            string full;
            try
            {
                full = System.IO.Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Set("Errore: percorso non valido.", err: true);
                return;
            }

            if (!Directory.Exists(full) || SvnService.FindSvnRoot(full) is null)
            {
                Set("Errore: non è una working copy SVN.", err: true);
                return;
            }

            path = full;
        }

        lock (_lock)
            _path = path;
        await LoadAsync(PageSize, ct);
    }

    private void Set(string msg, bool err, bool loading = false)
    {
        lock (_lock)
        {
            _msg = msg;
            _err = err;
            _loading = loading;
        }

        NotifyChanged();
    }

    /// <summary>Carica le prime <paramref name="limit"/> revisioni (<c>svn log --xml -v -l</c>).</summary>
    private async Task LoadAsync(int limit, CancellationToken ct)
    {
        string path;
        lock (_lock)
            path = _path;
        if (string.IsNullOrWhiteSpace(path))
        {
            Set("Inserisci il percorso della working copy.", err: false);
            return;
        }

        await _loadGate.WaitAsync(ct);
        try
        {
            Set("Caricamento log...", err: false, loading: true);
            var entries = await _svnService.GetLogAsync(path, limit, ct);

            var dtos = entries.Select(e => new LogEntryDto(
                e.Revision, e.Author, e.Date.ToString("o"), e.Message,
                (e.Paths ?? []).Select(p => new LogPathDto(NormalizeAction(p.Action), p.Path)).ToList())).ToList();

            lock (_lock)
            {
                _limit = limit;
                _entries = dtos;
                // Se svn ha restituito tutte quelle richieste, ce ne possono essere altre.
                _canMore = entries.Count >= limit;
                // La selezione sopravvive solo se la revisione e' ancora in elenco.
                if (_sel is { } sel && !dtos.Any(d => d.Rev == sel))
                {
                    _sel = null;
                    _diff = string.Empty;
                }
            }

            Set(dtos.Count == 0 ? "Nessuna revisione trovata." : $"{dtos.Count} revisioni caricate.", err: false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Set($"Errore: {ex.Message}", err: true);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private static string NormalizeAction(string action) => action is "A" or "M" or "D" or "R" ? action : "M";

    /// <summary>Carica il diff della revisione scelta (deve essere una di quelle caricate).</summary>
    private async Task SelectAsync(long revision, CancellationToken ct)
    {
        string path;
        lock (_lock)
        {
            if (!_entries.Any(e => e.Rev == revision))
                throw new ArgumentException($"Revisione {revision} non presente nell'elenco.");

            path = _path;
            _sel = revision;
            _diff = "Caricamento diff...";
        }

        Set($"Caricamento diff per revisione {revision}...", err: false, loading: true);
        try
        {
            var diff = await _svnService.GetDiffAsync(path, revision, ct);
            lock (_lock)
            {
                if (_sel == revision)
                    _diff = string.IsNullOrWhiteSpace(diff) ? "(nessuna differenza testuale per questa revisione)" : diff;
            }

            Set($"Diff revisione {revision} caricato.", err: false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            lock (_lock)
                _diff = $"Errore nel caricamento del diff: {ex.Message}";
            Set($"Errore diff: {ex.Message}", err: true);
        }
    }
}
