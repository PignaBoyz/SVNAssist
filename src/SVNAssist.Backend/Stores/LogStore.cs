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
    private string _msg = string.Empty;
    private bool _msgInitial = true; // il messaggio iniziale si compone al momento dello snapshot (segue la lingua corrente)
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
    // Opzioni di Show Log (come TortoiseSVN), valide per la sessione: stop su copy/rename acceso, revisioni di merge spento.
    private bool _stopOnCopy = true, _includeMerged;
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
    /// <param name="changes">Serve alla modalita' merge (working copy, messaggio di commit, "Annulla merge"); senza, il Log non ha il merge.</param>
    public LogStore(ISvnService svnService, ISvnAssistSettings settings, IHost? host = null, string? tempRoot = null, ChangesStore? changes = null)
    {
        if (changes is not null)
        {
            Merge = new MergeStore(svnService, changes);
            Merge.Changed += NotifyChanged;
        }

        _svnService = svnService;
        _settings = settings;
        _host = host;
        if (host is not null)
            _diffs = new DiffOpener(svnService, host, tempRoot);
    }

    /// <summary>Modalita' merge del Log (revisioni da unire, anteprima, merge, cherry-pick).</summary>
    internal MergeStore? Merge { get; }

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
        if (Merge?.GetSnapshot() is { } merge)
        {
            var (text, err) = Merge.Status;
            lock (_lock)
                return new LogState(_path, text, err, merge.Loading || merge.Busy, false, null, Merge.Entries, string.Empty, _filterText, null, merge);
        }

        lock (_lock)
            return new LogState(_path, _msgInitial ? Loc.T("Inserisci il percorso della working copy e premi Carica log.") : _msg, _err, _loading, _canMore, _sel, _entries, _diff, _filterText, FilesDto(), null, _stopOnCopy, _includeMerged);
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
        if (!_initialized && Merge?.Active != true)
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
            case "mergeToggle":
                Merge?.Toggle(Args.Long(args, "rev"));
                break;
            case "mergeClear":
                Merge?.ClearSelection();
                break;
            case "mergePreview":
                if (Merge is not null)
                    await Merge.PreviewAsync(ct);
                break;
            case "mergeAll":
                if (Merge is not null)
                    await Merge.RunAsync(all: true, ct);
                break;
            case "mergeSelected":
                if (Merge is not null)
                    await Merge.RunAsync(all: false, ct);
                break;
            case "mergeResolve":
                if (Merge is not null)
                    await Merge.ShowConflictsAsync();
                break;
            case "mergeRevertAll":
                if (Merge is not null)
                    await Merge.RevertAllAsync(ct);
                break;
            case "mergeUpdate":
                if (Merge is not null)
                    await Merge.UpdateAsync(ct);
                break;
            case "mergeClose":
                await CloseMergeAsync(ct);
                break;
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
            case "logOptions":
                lock (_lock)
                {
                    _stopOnCopy = Args.Bool(args, "stopOnCopy");
                    _includeMerged = Args.Bool(args, "merged");
                }

                await LoadAsync(PageSize, ct);
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
                throw new ArgumentException(Loc.T("Comando sconosciuto: {0}", name));
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

        Merge?.Close(); // il merge riguardava un'altra working copy
        lock (_lock)
            _path = svnRoot;
        await LoadAsync(PageSize, ct);
    }

    /// <summary>
    /// "SVN Merge...": il Log (documento nell'area editor, titolo "Merge da branches/x") mostra le revisioni di <paramref name="source"/> ancora da unire.
    /// Lo stato passa subito in modalita' merge, cosi' il <c>ready</c> di una finestra appena aperta non carica il log normale.
    /// </summary>
    internal async Task StartMergeAsync(string workingCopy, SwitchPickerStore.Target source, CancellationToken ct)
    {
        if (Merge is null)
            return;
        lock (_lock)
            _path = workingCopy;
        Merge.Begin(workingCopy, source);
        if (_host is not null)
            await _host.ShowLogWindowAsync(Merge.Title(source));
        await Merge.LoadAsync(ct);
    }

    /// <summary>Chiude il merge e torna al log normale della working copy.</summary>
    private async Task CloseMergeAsync(CancellationToken ct)
    {
        Merge?.Close();
        var path = CurrentPath;
        if (_host is not null && path.Length > 0)
        {
            var leaf = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            await _host.ShowLogWindowAsync(Loc.T("Log: {0}", leaf.Length > 0 ? leaf : path));
        }

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
            Set(Loc.T("Inserisci il percorso della working copy."), err: false);
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
                Set(Loc.T("Errore: percorso non valido."), err: true);
                return;
            }

            if (!Directory.Exists(full) || SvnService.FindSvnRoot(full) is null)
            {
                Set(Loc.T("Errore: non è una working copy SVN."), err: true);
                return;
            }

            path = full;
        }

        Merge?.Close();
        lock (_lock)
            _path = path;
        await LoadAsync(PageSize, ct);
    }

    private void Set(string msg, bool err, bool loading = false)
    {
        lock (_lock)
        {
            _msg = msg;
            _msgInitial = false;
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
            Set(Loc.T("Inserisci il percorso della working copy."), err: false);
            return;
        }

        await _loadGate.WaitAsync(ct);
        try
        {
            Set(Loc.T("Caricamento log..."), err: false, loading: true);
            var entries = await _svnService.GetLogAsync(path, limit, _filter with { StopOnCopy = _stopOnCopy, IncludeMerged = _includeMerged }, ct);

            var dtos = entries.SelectMany(e => Flatten(e, 0)).ToList();

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

            Set(dtos.Count == 0 ? Loc.T("Nessuna revisione trovata.") : Loc.T("{0} revisioni caricate{1}.", dtos.Count, _filter.IsEmpty ? "" : Loc.T(" (filtro attivo)")), err: false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Set(Loc.T("Errore: {0}", ex.Message), err: true);
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
                throw new InvalidOperationException(Loc.T("diff non disponibile"));
            await action();
        }
        catch (ArgumentException ex)
        {
            Set(Loc.T("Errore: {0}", ex.Message), err: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Set(Loc.T("Errore diff: {0}", ex.Message), err: true);
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
            var entry = _entries.FirstOrDefault(e => e.Rev == rev) ?? throw new ArgumentException(Loc.T("Revisione {0} non presente nell'elenco.", rev));
            if (index < 0 || index >= entry.Paths.Count)
                throw new ArgumentException(Loc.T("File non presente nella revisione."));
            file = entry.Paths[index];
        }

        if (file.K == "dir")
            throw new ArgumentException(Loc.T("è una cartella: scegli un file."));
        var url = await RepoRootAsync(path, ct) + file.P;
        var (l, r) = DiffOpener.ForRevision(file.A, rev);
        var name = System.IO.Path.GetFileName(file.P);
        await _diffs!.OpenRevisionsAsync(url, name, l, r, ct);
        Set(Loc.T("Diff aperto: {0}", DiffOpener.Title(name, l, r)), err: false);
    }

    /// <summary>Ctrl+clic su un'altra revisione: elenca i file cambiati tra le due (<c>svn diff --summarize</c>).</summary>
    private async Task CompareAsync(long a, long b, CancellationToken ct)
    {
        string path;
        lock (_lock)
        {
            if (!_entries.Any(e => e.Rev == a) || !_entries.Any(e => e.Rev == b))
                throw new ArgumentException(Loc.T("revisione non presente nell'elenco."));
            path = _path;
        }

        if (a > b)
            (a, b) = (b, a);
        Set(Loc.T("Confronto r{0} ↔ r{1}...", a, b), err: false, loading: true);
        var changes = await _svnService.GetChangedBetweenAsync(path, a, b, ct);
        lock (_lock)
        {
            _filesKind = "compare";
            _filesTitle = Loc.T("File cambiati tra r{0} e r{1}", a, b);
            _filesBase = path;
            _files = changes;
            _cmpFrom = a;
            _cmpTo = b;
        }

        Set(changes.Count == 0 ? Loc.T("Nessun file cambiato tra r{0} e r{1}.", a, b) : Loc.T("{0} file cambiati tra r{1} e r{2}.", changes.Count, a, b), err: false);
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
            throw new ArgumentException(Loc.T("percorso non valido."));
        }

        if (!Directory.Exists(folder) || SvnService.FindSvnRoot(folder) is not { } root)
            throw new ArgumentException(Loc.T("non è una working copy SVN."));

        Merge?.Close();
        if (CurrentPath.Length == 0)
        {
            lock (_lock)
                _path = root;
            await LoadAsync(PageSize, ct);
        }

        Set(Loc.T("Lettura delle modifiche..."), err: false, loading: true);
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
            _filesTitle = Loc.T("Modifiche in {0}", folder);
            _filesBase = folder;
            _files = changes;
        }

        Set(changes.Count == 0 ? Loc.T("Nessuna modifica nella cartella.") : Loc.T("{0} file modificati.", changes.Count), err: false);
    }

    private async Task OpenListedFileAsync(int index, CancellationToken ct)
    {
        string kind;
        SvnChange file;
        long from, to;
        lock (_lock)
        {
            if (_filesKind is null || index < 0 || index >= _files.Count)
                throw new ArgumentException(Loc.T("file non presente nell'elenco."));
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
                throw new ArgumentException(Loc.T("nessuna cartella selezionata."));
            folder = _filesBase;
        }

        var text = await _svnService.GetFileDiffAsync(folder, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            Set(Loc.T("Nessuna modifica."), err: false);
            return;
        }

        var file = await _diffs!.WriteTempAsync($"Diff {System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'))}.diff", text, ct);
        await _host!.OpenFileAsync(file);
    }

    internal static LogEntryDto ToDto(SvnLogEntry e, int depth = 0) => new(
        e.Revision, e.Author, e.Date.ToString("o"), e.Message,
        (e.Paths ?? []).Select(p => new LogPathDto(NormalizeAction(p.Action), p.Path, p.Kind == "dir" ? "dir" : null)).ToList(),
        depth, e.ReverseMerge);

    /// <summary>Elenco piatto in ordine di visita: la revisione, poi quelle entrate per merge (<c>Depth</c> &gt; 0).</summary>
    internal static IEnumerable<LogEntryDto> Flatten(SvnLogEntry e, int depth)
        => new[] { ToDto(e, depth) }.Concat((e.Children ?? []).SelectMany(c => Flatten(c, depth + 1)));

    private static string NormalizeAction(string action) => action is "A" or "M" or "D" or "R" ? action : "M";

    /// <summary>Carica il diff della revisione scelta (deve essere una di quelle caricate).</summary>
    private async Task SelectAsync(long revision, CancellationToken ct)
    {
        string path;
        bool merged;
        lock (_lock)
        {
            if (!_entries.Any(e => e.Rev == revision))
                throw new ArgumentException(Loc.T("Revisione {0} non presente nell'elenco.", revision));

            path = _path;
            merged = _entries.First(e => e.Rev == revision).Depth > 0;
            _sel = revision;
            _diff = Loc.T("Caricamento diff...");
        }

        Set(Loc.T("Caricamento diff per revisione {0}...", revision), err: false, loading: true);
        try
        {
            // una revisione entrata per merge sta su un altro ramo: il diff -c si chiede alla radice del repository
            var diff = await _svnService.GetDiffAsync(merged ? await RepoRootAsync(path, ct) : path, revision, ct);
            lock (_lock)
            {
                if (_sel == revision)
                    _diff = string.IsNullOrWhiteSpace(diff) ? Loc.T("(nessuna differenza testuale per questa revisione)") : diff;
            }

            Set(Loc.T("Diff revisione {0} caricato.", revision), err: false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            lock (_lock)
                _diff = Loc.T("Errore nel caricamento del diff: {0}", ex.Message);
            Set(Loc.T("Errore diff: {0}", ex.Message), err: true);
        }
    }
}
