using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using SVNAssist.Models;
using SVNAssist.Rpc;
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
    private LogFilterDto _filterText = new("", "", "", "", "");
    private SvnLogFilter _filter = new();
    private string? _repoRoot;
    private string? _repoRootFor;
    // Elenco file (confronto tra revisioni o modifiche di una cartella): Kind "folder" | "compare".
    private string? _filesKind;
    private string _filesTitle = string.Empty;
    private string _filesBase = string.Empty;
    private long _cmpFrom, _cmpTo;
    private IReadOnlyList<SvnChange> _files = [];
    private readonly IHost? _host;
    private readonly DiffOpener? _diffs;

    /// <param name="host">Serve ad aprire i diff nell'editor di VS; senza host (test) i comandi di diff falliscono con un messaggio.</param>
    public LogStore(ISvnService svnService, ISvnAssistSettings settings, IHost? host = null, string? tempRoot = null)
    {
        _svnService = svnService;
        _settings = settings;
        _host = host;
        if (host is not null)
            _diffs = new DiffOpener(svnService, host, tempRoot);
    }

    /// <summary>Cancella i file temporanei dei diff aperti (chiusura del backend).</summary>
    internal void CleanTemp() => _diffs?.Dispose();

    /// <summary>Percorso della working copy (o URL del repository) interrogata.</summary>
    internal string CurrentPath
    {
        get { lock (_lock) return _path; }
    }

    /// <inheritdoc />
    internal override object GetSnapshot()
    {
        lock (_lock)
            return new LogState(_path, _msg, _err, _loading, _canMore, _sel, _entries, _diff, _filterText, FilesDto());
    }

    private LogFilesDto? FilesDto() => _filesKind is null
        ? null
        : new LogFilesDto(_filesKind, _filesTitle, _files.Select(f => new LogPathDto(f.Action, ShortPath(f.Path), "file")).ToList());

    private string ShortPath(string p)
    {
        if (_filesBase.Length > 0 && p.StartsWith(_filesBase, StringComparison.OrdinalIgnoreCase))
            return p[_filesBase.Length..].TrimStart('\\', '/');
        return p;
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
                CloseFiles();
                await SelectAsync(Args.Long(args, "rev"), ct);
                break;
            case "logFilter":
                SetFilter(args);
                await LoadAsync(PageSize, ct);
                break;
            case "logOpenFile":
                await GuardAsync(() => OpenRevisionFileAsync(Args.Long(args, "rev"), (int)Args.Long(args, "i"), ct));
                break;
            case "logCompare":
                await GuardAsync(() => CompareAsync(Args.Long(args, "a"), Args.Long(args, "b"), ct));
                break;
            case "logFolder":
                await GuardAsync(() => ShowFolderAsync(Args.Str(args, "path"), ct));
                break;
            case "logFileOpen":
                await GuardAsync(() => OpenListedFileAsync((int)Args.Long(args, "i"), ct));
                break;
            case "logFilesPatch":
                await GuardAsync(() => OpenPatchAsync(ct));
                break;
            case "logFilesClose":
                CloseFiles();
                NotifyChanged();
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
            var entries = await _svnService.GetLogAsync(path, limit, _filter, ct);

            var dtos = entries.Select(e => new LogEntryDto(
                e.Revision, e.Author, e.Date.ToString("o"), e.Message,
                (e.Paths ?? []).Select(p => new LogPathDto(NormalizeAction(p.Action), p.Path, p.Kind == "dir" ? "dir" : null)).ToList())).ToList();

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

            Set(dtos.Count == 0 ? "Nessuna revisione trovata." : $"{dtos.Count} revisioni caricate{(_filter.IsEmpty ? "" : " (filtro attivo)")}.", err: false);
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

    private void SetFilter(JsonElement args)
    {
        string Get(string n) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";
        static DateOnly? Day(string s) => DateOnly.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
        var f = new LogFilterDto(Get("author"), Get("text"), Get("path"), Get("from"), Get("to"));
        var from = Day(f.From);
        var to = Day(f.To);
        if (from > to)
        {
            (from, to) = (to, from);
            f = f with { From = f.To, To = f.From };
        }

        lock (_lock)
        {
            _filterText = f;
            _filter = new SvnLogFilter(f.Author, f.Text, f.Path, from, to);
        }
    }

    private void CloseFiles()
    {
        lock (_lock)
        {
            _filesKind = null;
            _files = [];
        }
    }

    /// <summary>Esegue un comando di diff: gli errori diventano il messaggio della vista (mai eccezioni verso l'host).</summary>
    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            if (_diffs is null || _host is null)
                throw new InvalidOperationException("diff non disponibile");
            await action();
        }
        catch (ArgumentException ex)
        {
            Set($"Errore: {ex.Message}", err: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Set($"Errore diff: {ex.Message}", err: true);
        }
    }

    private async Task<string> RepoRootAsync(string path, CancellationToken ct)
    {
        if (_repoRootFor != path || _repoRoot is null)
        {
            _repoRoot = (await _svnService.GetRepositoryRootAsync(path, ct)).TrimEnd('/');
            _repoRootFor = path;
        }

        return _repoRoot;
    }

    /// <summary>Clic su un file di una revisione: diff nativo <c>r-1 ↔ r</c> (per i file aggiunti/eliminati un lato e' vuoto).</summary>
    private async Task OpenRevisionFileAsync(long rev, int index, CancellationToken ct)
    {
        string path;
        LogPathDto file;
        lock (_lock)
        {
            path = _path;
            var entry = _entries.FirstOrDefault(e => e.Rev == rev) ?? throw new ArgumentException($"Revisione {rev} non presente nell'elenco.");
            if (index < 0 || index >= entry.Paths.Count)
                throw new ArgumentException("File non presente nella revisione.");
            file = entry.Paths[index];
        }

        if (file.K == "dir")
            throw new ArgumentException("è una cartella: scegli un file.");
        var url = await RepoRootAsync(path, ct) + file.P;
        var (l, r) = DiffOpener.ForRevision(file.A, rev);
        var name = System.IO.Path.GetFileName(file.P);
        await _diffs!.OpenRevisionsAsync(url, name, l, r, ct);
        Set($"Diff aperto: {DiffOpener.Title(name, l, r)}", err: false);
    }

    /// <summary>Ctrl+clic su un'altra revisione: elenca i file cambiati tra le due (<c>svn diff --summarize</c>).</summary>
    private async Task CompareAsync(long a, long b, CancellationToken ct)
    {
        string path;
        lock (_lock)
        {
            if (!_entries.Any(e => e.Rev == a) || !_entries.Any(e => e.Rev == b))
                throw new ArgumentException("revisione non presente nell'elenco.");
            path = _path;
        }

        if (a > b)
            (a, b) = (b, a);
        Set($"Confronto r{a} ↔ r{b}...", err: false, loading: true);
        var changes = await _svnService.GetChangedBetweenAsync(path, a, b, ct);
        lock (_lock)
        {
            _filesKind = "compare";
            _filesTitle = $"File cambiati tra r{a} e r{b}";
            _filesBase = path;
            _files = changes;
            _cmpFrom = a;
            _cmpTo = b;
        }

        Set(changes.Count == 0 ? $"Nessun file cambiato tra r{a} e r{b}." : $"{changes.Count} file cambiati tra r{a} e r{b}.", err: false);
    }

    /// <summary>Diff di cartella: elenco dei file modificati (<c>svn status</c>); ogni clic apre il diff nativo BASE ↔ working copy.</summary>
    private async Task ShowFolderAsync(string typed, CancellationToken ct)
    {
        string folder;
        try
        {
            folder = System.IO.Path.GetFullPath(typed.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("percorso non valido.");
        }

        if (!Directory.Exists(folder) || SvnService.FindSvnRoot(folder) is not { } root)
            throw new ArgumentException("non è una working copy SVN.");

        if (CurrentPath.Length == 0)
        {
            lock (_lock)
                _path = root;
            await LoadAsync(PageSize, ct);
        }

        Set("Lettura delle modifiche...", err: false, loading: true);
        var changes = new List<SvnChange>();
        foreach (var s in await _svnService.GetStatusAsync(folder, ct))
        {
            var action = s.Status switch
            {
                SvnStatusKind.Added => "A",
                SvnStatusKind.Deleted or SvnStatusKind.Missing => "D",
                SvnStatusKind.Replaced => "R",
                SvnStatusKind.Modified or SvnStatusKind.Conflicted => "M",
                _ => null,
            };
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, s.Path));
            if (action is not null && !Directory.Exists(full))
                changes.Add(new SvnChange(action, full));
        }

        lock (_lock)
        {
            _filesKind = "folder";
            _filesTitle = $"Modifiche in {folder}";
            _filesBase = folder;
            _files = changes;
        }

        Set(changes.Count == 0 ? "Nessuna modifica nella cartella." : $"{changes.Count} file modificati.", err: false);
    }

    private async Task OpenListedFileAsync(int index, CancellationToken ct)
    {
        string kind;
        SvnChange file;
        long from, to;
        lock (_lock)
        {
            if (_filesKind is null || index < 0 || index >= _files.Count)
                throw new ArgumentException("file non presente nell'elenco.");
            kind = _filesKind;
            file = _files[index];
            (from, to) = (_cmpFrom, _cmpTo);
        }

        if (kind == "folder")
        {
            await _diffs!.OpenWorkingAsync(file.Path, ct);
            return;
        }

        var (l, r) = DiffOpener.ForRange(file.Action, from, to);
        await _diffs!.OpenRevisionsAsync(file.Path, System.IO.Path.GetFileName(file.Path), l, r, ct);
    }

    /// <summary>"Apri come patch": il vecchio .diff unico della cartella, aperto come documento.</summary>
    private async Task OpenPatchAsync(CancellationToken ct)
    {
        string folder;
        lock (_lock)
        {
            if (_filesKind != "folder")
                throw new ArgumentException("nessuna cartella selezionata.");
            folder = _filesBase;
        }

        var text = await _svnService.GetFileDiffAsync(folder, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            Set("Nessuna modifica.", err: false);
            return;
        }

        var file = await _diffs!.WriteTempAsync($"Diff {System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'))}.diff", text, ct);
        await _host!.OpenFileAsync(file);
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
