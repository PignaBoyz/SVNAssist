using System.Collections.Concurrent;
using System.Diagnostics;
using SVNAssist.Models;
using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>
/// "SVN Merge..." da un branch: il Log in modalita' merge mostra le revisioni ancora da unire (<c>svn mergeinfo --show-revs eligible</c>)
/// e da li si fa l'anteprima (<c>--dry-run</c>), il merge di tutto o il cherry-pick delle revisioni scelte (<c>-c</c>).
/// Il merge parte sempre dalla radice della working copy, con <c>--accept postpone</c>, e non committa: i conflitti compaiono in Changes,
/// il messaggio di commit va nei Recenti e un banner offre "Annulla merge" (revert ricorsivo, <see cref="ChangesStore"/>).
/// Il merge richiede una working copy senza modifiche locali, cosi' annullarlo non porta via lavoro non correlato.
/// </summary>
internal sealed class MergeStore
{
    private readonly ISvnService _svn;
    private readonly ChangesStore _changes;
    private readonly object _lock = new();
    private readonly HashSet<long> _selected = [];
    // Autore, data e messaggio gia' letti (per revisione): dopo un merge, un annulla o un update si rilegge solo mergeinfo e si chiede il log delle revisioni nuove.
    private readonly Dictionary<long, LogEntryDto> _logCache = [];
    // File toccati da ogni revisione (svn log -v), letti solo per le revisioni di cui serve l'elenco e poi in cache: una revisione non cambia.
    private readonly Dictionary<long, IReadOnlyList<SvnLogPath>> _paths = [];
    private long? _focus;   // revisione scelta dal menu contestuale per vederne i file (vale al posto della scelta)
    private long _filesGen;
    private bool _filesLoading;
    private string _filesError = string.Empty;
    private MergeFilesDto? _filesDto;
    private CancellationTokenSource _filesCts = new();
    private string? _repoRoot;

    private bool _active, _loading, _busy;
    private string _wc = string.Empty, _error = string.Empty, _blocked = string.Empty, _message = string.Empty;
    private SwitchPickerStore.Target? _source;
    private IReadOnlyList<LogEntryDto> _entries = [];
    private MergeResultDto? _result;
    private IReadOnlyList<SvnFileStatus> _pending = []; // modifiche pendenti (M, A, D, R) dell'ultimo controllo

    public MergeStore(ISvnService svn, ChangesStore changes)
    {
        _svn = svn;
        _changes = changes;
        _changes.Changed += () => { if (Active) Changed?.Invoke(); }; // "da committare" cambia con le modifiche della working copy
    }

    public event Action? Changed;

    internal bool Active { get { lock (_lock) return _active; } }

    /// <summary>Revisioni da unire (per la tabella del Log).</summary>
    internal IReadOnlyList<LogEntryDto> Entries { get { lock (_lock) return _entries; } }

    /// <summary>Messaggio della barra di stato del Log in modalita' merge e se e' un errore.</summary>
    internal (string Text, bool Err) Status
    {
        get
        {
            lock (_lock)
                return _error.Length > 0 ? (string.Empty, false) : (_message, false); // l'errore sta gia' nel pannello: non si ripete nella riga in fondo
        }
    }

    private const int MaxAppliedInfo = 100;

    internal string Title(SwitchPickerStore.Target source) => Loc.T("Merge da {0}", source.Name);

    /// <summary>"Risolvi i conflitti": porta in primo piano Changes, dove c'e' la sezione Conflitti.</summary>
    internal Task ShowConflictsAsync() => _changes.ShowWindowAsync();

    internal MergeDto? GetSnapshot()
    {
        var pending = _changes.MergePending; // prima del lock: prende il lock di ChangesStore
        var interrupted = _changes.BuildPendingMerge();
        lock (_lock)
        {
            if (!_active || _source is null)
                return null;
            // Merge interrotto: quali revisioni sono gia' state applicate (autore e messaggio dalla cache; se mancano arrivano a breve da svn log).
            var applied = interrupted is null ? null
                : interrupted.AppliedRevs.Take(MaxAppliedInfo).Select(r => _logCache.TryGetValue(r, out var d) ? new MergeAppliedDto(r, d.Author, (d.Msg ?? string.Empty).Split('\n')[0]) : new MergeAppliedDto(r, string.Empty, string.Empty)).ToList();
            return new MergeDto(_source.Name, Title(_source), _loading, _busy, _error, _blocked, _selected.Order().ToList(), _entries.Count, _result, pending, SvnErrors.SuggestsUpdate(_error), _filesDto, _pending.Count, interrupted, applied);
        }
    }

    internal void Close()
    {
        lock (_lock)
        {
            _active = false;
            _entries = [];
            _selected.Clear();
            _result = null;
            ResetFiles();
        }

        Changed?.Invoke();
    }

    /// <summary>Il Log passa in modalita' merge (subito, senza rete) e poi carica le revisioni da unire.</summary>
    internal void Begin(string workingCopy, SwitchPickerStore.Target source)
    {
        lock (_lock)
        {
            _active = true;
            _loading = true;
            _busy = false;
            _wc = workingCopy;
            _source = source;
            _entries = [];
            _logCache.Clear();
            _selected.Clear();
            _result = null;
            ResetFiles();
            _error = string.Empty;
            _blocked = string.Empty;
            _message = Loc.T("Lettura delle revisioni da unire...");
        }

        Changed?.Invoke();
    }

    internal async Task LoadAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var times = new ConcurrentDictionary<string, long>();
        try
        {
            // Tre letture indipendenti (versione di svn, revisioni da unire, stato della working copy) in parallelo: prima erano in fila.
            var version = Timed(times, "version", () => _svn.GetSvnVersionAsync(ct));
            var eligible = ReloadEligibleAsync(ct, times);
            var blocked = Timed(times, "status", () => CheckBlockedAsync(ct));
            var all = Task.WhenAll(version, eligible, blocked);
            if (await version is { } v && v < new Version(1, 8))
            {
                try { await all; } catch (Exception) { /* l'errore che conta e' la versione */ }
                lock (_lock)
                    _entries = [];
                throw new InvalidOperationException(Loc.T("SVN Merge richiede svn 1.8 o successivo (trovato {0}).", v));
            }

            await all;
            BackendLog.Info($"merge load: {Format(times)}, totale {sw.ElapsedMilliseconds} ms, {Entries.Count} revisioni da unire");
            _ = LoadAppliedInfoAsync(ct); // merge interrotto: autore e messaggio delle revisioni gia' applicate
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BackendLog.Warn($"merge load fallito dopo {sw.ElapsedMilliseconds} ms ({Format(times)}): {ex.Message}");
            lock (_lock)
                _error = Loc.T("Errore: {0}", ex.Message);
        }
        finally
        {
            lock (_lock)
                _loading = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Merge interrotto: legge autore e messaggio delle revisioni gia' applicate (non sono piu' nell'elenco da unire) per mostrarle "applicate in questo merge".</summary>
    private async Task LoadAppliedInfoAsync(CancellationToken ct)
    {
        try
        {
            if (_changes.CurrentPendingMerge is not { } e || _source is null)
                return;
            var source = new SwitchPickerStore.Target(e.Name, string.Empty, e.Url, false, e.Path);
            await EnsureLogAsync(e.Applied.Take(MaxAppliedInfo).ToList(), source, ct);
            Changed?.Invoke();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BackendLog.Warn("merge: log delle revisioni applicate non letto: " + ex.Message.Split('\n')[0]);
        }
    }

    /// <summary>Esegue <paramref name="body"/> e ne registra la durata (ms) sotto <paramref name="name"/>, anche in caso di errore.</summary>
    private static async Task<T> Timed<T>(ConcurrentDictionary<string, long> times, string name, Func<Task<T>> body)
    {
        var sw = Stopwatch.StartNew();
        try { return await body(); }
        finally { times.AddOrUpdate(name, sw.ElapsedMilliseconds, (_, old) => old + sw.ElapsedMilliseconds); }
    }

    private static async Task TimedRun(ConcurrentDictionary<string, long> times, string name, Func<Task> body)
    {
        var sw = Stopwatch.StartNew();
        try { await body(); }
        finally { times.AddOrUpdate(name, sw.ElapsedMilliseconds, (_, old) => old + sw.ElapsedMilliseconds); }
    }

    private static string Format(ConcurrentDictionary<string, long> times)
        => string.Join(", ", times.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key} {t.Value} ms"));

    /// <summary>Rilegge le revisioni da unire e la working copy (dopo "Annulla merge"): l'esito precedente non vale piu'.</summary>
    internal async Task RefreshAsync(CancellationToken ct)
    {
        if (!Active)
            return;
        var sw = Stopwatch.StartNew();
        var times = new ConcurrentDictionary<string, long>();
        try
        {
            await Task.WhenAll(ReloadEligibleAsync(ct, times), Timed(times, "status", () => CheckBlockedAsync(ct)));
            lock (_lock)
                _result = null;
            BackendLog.Info($"merge refresh: {Format(times)}, totale {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Fail(ex);
        }

        Changed?.Invoke();
    }

    /// <summary>"SVN Revert di tutto..." dall'avviso: conferma con l'elenco (in Changes), poi rilegge revisioni e avviso, cosi' il merge si puo' rifare.</summary>
    internal async Task RevertAllAsync(CancellationToken ct)
    {
        if (!Active)
            return;
        await _changes.RevertAllAsync(ct);
        await RefreshAsync(ct);
    }

    /// <summary>"SVN Update ora" dall'errore di revisioni miste: aggiorna la working copy e ricalcola le revisioni da unire.</summary>
    internal async Task UpdateAsync(CancellationToken ct)
    {
        if (!Active)
            return;
        await _changes.UpdateNowAsync(ct);
        lock (_lock)
            _error = string.Empty;
        await RefreshAsync(ct);
    }

    internal void Toggle(long rev)
    {
        lock (_lock)
        {
            if (_busy || !_entries.Any(e => e.Rev == rev))
                return;
            if (!_selected.Remove(rev))
                _selected.Add(rev);
            _result = null; // l'anteprima riguardava un'altra scelta
            _focus = null;
        }

        RequestFiles();
    }

    internal void ClearSelection()
    {
        lock (_lock)
        {
            _selected.Clear();
            _result = null;
            _focus = null;
        }

        RequestFiles();
    }

    /// <summary>Menu contestuale: lascia la scelta com'e' e mostra i file di una sola revisione (<paramref name="rev"/> 0 = torna alla scelta).</summary>
    internal void Focus(long rev)
    {
        lock (_lock)
        {
            if (rev != 0 && !_entries.Any(e => e.Rev == rev))
                return;
            _focus = rev == 0 ? null : rev;
        }

        RequestFiles();
    }

    /// <summary>Menu contestuale "Anteprima di questa" / "Merge di questa revisione": la scelta diventa solo questa revisione.</summary>
    private bool SelectOnly(long rev)
    {
        lock (_lock)
        {
            if (_busy || _loading || !_active || !_entries.Any(e => e.Rev == rev))
                return false;
            _selected.Clear();
            _selected.Add(rev);
            _result = null;
            _focus = null;
        }

        RequestFiles();
        return true;
    }

    internal Task PreviewRevisionAsync(long rev, CancellationToken ct) => SelectOnly(rev) ? PreviewAsync(ct) : Task.CompletedTask;

    internal Task MergeRevisionAsync(long rev, CancellationToken ct) => SelectOnly(rev) ? RunAsync(all: false, ct) : Task.CompletedTask;

    // ── File coinvolti (bug 6) ───────────────────────────────────────

    /// <summary>Svuota tutto quello che riguarda i file (nuovo merge, chiusura). Chiamare col lock preso.</summary>
    private void ResetFiles()
    {
        _paths.Clear();
        _focus = null;
        _filesDto = null;
        _filesLoading = false;
        _filesError = string.Empty;
        _repoRoot = null;
        _filesGen++;
        var old = _filesCts;
        _filesCts = new CancellationTokenSource();
        try { old.Cancel(); } catch (ObjectDisposedException) { /* gia' chiuso */ }
    }

    private List<long> SubjectRevs() => _focus is { } f ? [f] : _selected.Order().ToList();

    /// <summary>Ricostruisce l'elenco dalla cache. Chiamare col lock preso.</summary>
    private void RebuildFiles()
    {
        var revs = SubjectRevs();
        if (revs.Count == 0 || _source is null)
        {
            _filesDto = null;
            return;
        }

        var subject = _focus is { } f ? $"r{f}" : RevsLabel(revs);
        if (_filesLoading)
        {
            _filesDto = new MergeFilesDto(subject, _focus is not null, true, string.Empty, [], 0, 0);
            return;
        }

        var all = MergeFiles.Union(revs, r => _paths.GetValueOrDefault(r), _source.Path);
        var (items, more) = MergeFiles.Cap(all, MergeFiles.MaxItems);
        _filesDto = new MergeFilesDto(subject, _focus is not null, false, _filesError, items, all.Count(i => i.K is null), more);
    }

    /// <summary>La scelta (o la revisione del menu) e' cambiata: i file mancanti in cache si leggono in background (poche richieste, dopo una breve attesa per le raffiche di clic).</summary>
    private void RequestFiles()
    {
        long gen;
        List<long> missing;
        CancellationToken ct;
        lock (_lock)
        {
            gen = ++_filesGen;
            _filesError = string.Empty;
            missing = SubjectRevs().Where(r => !_paths.ContainsKey(r)).ToList();
            _filesLoading = missing.Count > 0;
            RebuildFiles();
            ct = _filesCts.Token;
        }

        Changed?.Invoke();
        if (missing.Count > 0)
            _ = LoadFilesAsync(gen, missing, ct);
    }

    private async Task LoadFilesAsync(long gen, List<long> missing, CancellationToken ct)
    {
        try
        {
            await Task.Delay(FilesDebounce, ct);
            lock (_lock)
            {
                if (gen != _filesGen)
                    return; // una scelta piu' recente ha gia' chiesto quello che le serve
            }

            await EnsurePathsAsync(missing, ct);
            lock (_lock)
            {
                if (gen != _filesGen)
                    return;
                _filesLoading = false;
                RebuildFiles();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            BackendLog.Warn("merge files: " + ex.Message);
            lock (_lock)
            {
                if (gen != _filesGen)
                    return;
                _filesLoading = false;
                _filesError = Loc.T("Errore: {0}", ex.Message);
                RebuildFiles();
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Attesa prima di leggere i file dopo un clic (sostituibile nei test).</summary>
    internal static TimeSpan FilesDebounce { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Mette in cache i file toccati dalle revisioni indicate: <c>svn log -v</c> a gruppi di revisioni adiacenti nell'elenco (nessuna revisione in mezzo
    /// da scartare, una richiesta per gruppo, in parallelo) e non una chiamata per revisione.
    /// </summary>
    private Task EnsurePathsAsync(IReadOnlyList<long> revs, CancellationToken ct) => EnsurePathsAsync(revs, _source!, ct);

    private async Task EnsurePathsAsync(IReadOnlyList<long> revs, SwitchPickerStore.Target source, CancellationToken ct)
    {
        List<long> missing;
        Dictionary<long, int> index;
        lock (_lock)
        {
            missing = revs.Where(r => !_paths.ContainsKey(r)).Distinct().ToList();
            index = [];
            for (var i = 0; i < _entries.Count; i++)
                index[_entries[i].Rev] = i;
        }

        if (missing.Count == 0)
            return;
        var sw = Stopwatch.StartNew();
        var runs = new List<(long Min, long Max)>();
        if (missing.Any(r => !index.ContainsKey(r)))
            runs = GroupRevisions(missing.Order()); // senza l'elenco da unire (merge ripreso da Changes): per vicinanza di numero
        else
        {
            int? prev = null;
            foreach (var r in missing.OrderBy(r => index[r]))
            {
                var i = index[r];
                if (prev is { } p && i == p + 1)
                    runs[^1] = (Math.Min(runs[^1].Min, r), Math.Max(runs[^1].Max, r));
                else
                    runs.Add((r, r));
                prev = i;
            }
        }

        using var gate = new SemaphoreSlim(LogParallelism);
        var batches = await Task.WhenAll(runs.Select(async g =>
        {
            await gate.WaitAsync(ct);
            try { return await _svn.GetLogRangeAsync(source.Url, g.Min, g.Max, ct, withPaths: true); }
            finally { gate.Release(); }
        }));
        var wanted = missing.ToHashSet();
        lock (_lock)
        {
            foreach (var e in batches.SelectMany(b => b).Where(e => wanted.Contains(e.Revision)))
                _paths[e.Revision] = e.Paths ?? [];
            foreach (var r in missing)
                _paths.TryAdd(r, []); // il log non l'ha restituita (permessi): elenco vuoto, non si richiede di nuovo
        }

        BackendLog.Info($"merge files: {missing.Count} revisioni in {runs.Count} richieste di log -v, {sw.ElapsedMilliseconds} ms");
    }

    private async Task<string> RepoRootAsync(CancellationToken ct)
    {
        SwitchPickerStore.Target source;
        lock (_lock)
        {
            if (_repoRoot is not null)
                return _repoRoot;
            source = _source!;
        }

        var root = (await _svn.GetRepositoryRootAsync(source.Url, ct)).TrimEnd('/');
        lock (_lock)
            _repoRoot = root;
        return root;
    }

    /// <summary>
    /// Clic su un file dell'elenco: URL, nome e revisioni (sinistra, destra) per il diff. Con <paramref name="rev"/> (elenco per revisione dell'anteprima) <c>r-1 ↔ r</c>;
    /// altrimenti prima della prima revisione che lo tocca ↔ dopo l'ultima (comprende anche eventuali revisioni intermedie non scelte).
    /// </summary>
    internal async Task<(string Url, string Name, long? Left, long? Right)> ResolveFileAsync(long? rev, string rel, CancellationToken ct)
    {
        List<long> revs;
        string sourcePath;
        lock (_lock)
        {
            revs = rev is { } r ? [r] : SubjectRevs();
            sourcePath = _source?.Path ?? throw new ArgumentException(Loc.T("Nessun merge in corso."));
        }

        await EnsurePathsAsync(revs, ct);
        var hits = new List<(long Rev, SvnLogPath P)>();
        lock (_lock)
        {
            foreach (var r in revs.Order())
            foreach (var p in _paths.GetValueOrDefault(r) ?? [])
                if (string.Equals(MergeFiles.Relative(p.Path, sourcePath), rel, StringComparison.Ordinal))
                    hits.Add((r, p));
        }

        if (hits.Count == 0 || hits[^1].P.Kind == "dir")
            throw new ArgumentException(Loc.T("File non presente nelle revisioni scelte."));
        var root = await RepoRootAsync(ct);
        var url = root + hits[^1].P.Path;
        var name = System.IO.Path.GetFileName(rel);
        if (hits.Count == 1)
        {
            var (l, r2) = DiffOpener.ForRevision(hits[0].P.Action, hits[0].Rev);
            return (url, name, l, r2);
        }

        var net = MergeFiles.NetAction(hits[0].P.Action, hits[^1].P.Action) ?? "M";
        var (left, right) = DiffOpener.ForRange(net, hits[0].Rev - 1, hits[^1].Rev);
        return (url, name, left, right);
    }

    /// <summary>Anteprima (<c>--dry-run</c>): le revisioni scelte, o tutte quelle da unire se non ce n'e' nessuna.</summary>
    internal async Task PreviewAsync(CancellationToken ct)
    {
        if (!TryBusy(out var source, out var wc, out var revs))
            return;
        try
        {
            var sw = Stopwatch.StartNew();
            var times = new ConcurrentDictionary<string, long>();
            // L'anteprima non modifica nulla: lo stato (solo per aggiornare l'avviso) si legge in parallelo al dry-run.
            var blocked = Timed(times, "status", () => CheckBlockedAsync(ct));
            var merge = Timed(times, "dry-run", () => _svn.MergeAsync(wc, source.Url, revs, dryRun: true, ct));
            // Per revisione: i file di ogni revisione (svn log -v, in cache) piu' il dry-run complessivo per i conflitti. Un dry-run per revisione
            // sarebbe N visite della working copy (e non prevederebbe comunque i conflitti tra revisioni successive).
            var shown = (revs ?? Entries.Select(e => e.Rev).Order().ToArray()).Take(MergeFiles.MaxRevisionsInPreview).ToList();
            var paths = Timed(times, "files", async () => { await EnsurePathsAsync(shown, ct); return 0; });
            MergeOutput output;
            string? incomplete = null;
            try
            {
                await Task.WhenAll(blocked, merge, paths);
                output = await merge;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && SvnErrors.IsMergeStoppedByConflict(ex.Message))
            {
                // Il dry-run applica una revisione alla volta senza vedere le precedenti e si ferma al primo conflitto: i file di ogni revisione (log -v) si mostrano lo stesso.
                try { await Task.WhenAll(blocked, paths); } catch (Exception) { /* l'errore che conta e' il conflitto */ }
                output = new MergeOutput([], 0, 0);
                incomplete = SvnErrors.ConflictingRevision(ex.Message) is { } r
                    ? Loc.T("Anteprima incompleta: il dry-run si è fermato alla revisione r{0}, che va in conflitto, e le successive non sono state simulate. I file di ogni revisione sono elencati qui sotto, ma i conflitti previsti arrivano solo fino a lì.", r)
                    : Loc.T("Anteprima incompleta: il dry-run si è fermato alla prima revisione in conflitto e le successive non sono state simulate. I file di ogni revisione sono elencati qui sotto, ma i conflitti previsti arrivano solo fino a lì.");
            }

            var total = revs?.Length ?? Entries.Count;
            var result = ToResult("preview", revs is null ? AllLabel() : RevsLabel(revs), output) with { ByRev = ByRevision(shown, output), ByRevMore = Math.Max(0, total - shown.Count) };
            lock (_lock)
            {
                _result = result;
                if (incomplete is not null)
                    _error = incomplete;
            }

            Say(Loc.T("Anteprima completata."));
            BackendLog.Info($"merge preview: {Format(times)}, totale {sw.ElapsedMilliseconds} ms, {output.Items.Count} file");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Fail(ex);
        }
        finally
        {
            Idle();
        }
    }

    /// <summary>Merge vero: tutto quello che e' da unire (<paramref name="all"/>) oppure il cherry-pick delle revisioni scelte. Una revisione alla volta (a gruppi dove sicuro, vedi <see cref="MergeRunner"/>), con stato su disco.</summary>
    internal async Task RunAsync(bool all, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_busy || _loading || !_active)
                return;
            if (!all && _selected.Count == 0)
            {
                _error = Loc.T("Seleziona almeno una revisione da unire.");
                Changed?.Invoke();
                return;
            }
        }

        if (_changes.CurrentPendingMerge is not null)
        {
            lock (_lock)
                _error = Loc.T("C'è un merge interrotto: continualo (\"Continua merge\") oppure annullalo prima di iniziarne un altro.");
            Changed?.Invoke();
            return;
        }

        if (!TryBusy(out var source, out var wc, out var picked, forMerge: true))
            return;
        try
        {
            long[] revs;
            lock (_lock)
                revs = all || picked is null ? _entries.Select(e => e.Rev).Order().ToArray() : picked;
            if (revs.Length == 0)
            {
                Say(Loc.T("Nessuna revisione da unire: {0} è già allineato.", source.Name));
                return;
            }

            var total = Stopwatch.StartNew();
            var times = new ConcurrentDictionary<string, long>();
            // Il merge richiede una working copy senza elementi che svn non ripristina: lo si ricontrolla sempre, e' cambiata dall'ultima volta.
            if (await Timed(times, "status", () => CheckBlockedAsync(ct)))
                return;

            PreMergeSnapshot? snapshot = null;
            await _changes.RunExclusiveAsync(async () =>
            {
                // Modifiche pendenti: avviso con i file in comune (dry-run) e copia di sicurezza PRIMA di toccare qualcosa; senza copia il merge non parte.
                List<SvnFileStatus> dirty;
                lock (_lock)
                    dirty = _pending.ToList();
                if (dirty.Count > 0)
                {
                    // I file in comune: dal dry-run. Un E155015 qui NON e' un merge fermato (non e' stato toccato nulla: il dry-run applica una revisione alla volta
                    // senza vedere le precedenti): i file in comune si ricavano allora dal log. Mai verso i gestori del merge vero.
                    List<string> overlap;
                    var known = true;
                    try
                    {
                        var dry = await Timed(times, "dry-run", () => _svn.MergeAsync(wc, source.Url, all ? null : revs, dryRun: true, ct));
                        overlap = Overlap(dirty, dry);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested && SvnErrors.IsMergeStoppedByConflict(ex.Message))
                    {
                        BackendLog.Info("merge: il dry-run dei file in comune si e' fermato su un conflitto, si usa il log: " + ex.Message.Split('\n')[0]);
                        try { overlap = await OverlapFromLogAsync(dirty, all ? null : revs, ct); }
                        catch (Exception ex2) when (!ct.IsCancellationRequested)
                        {
                            BackendLog.Warn("merge: file in comune non prevedibili: " + ex2.Message);
                            (overlap, known) = ([], false);
                        }
                    }

                    if (!await _changes.ConfirmAsync(PendingMergeText(dirty.Count, overlap, known), ct))
                    {
                        Say(Loc.T("Merge non eseguito: le modifiche pendenti non sono state toccate."));
                        return;
                    }

                    try { snapshot = await Timed(times, "backup", () => PreMergeSnapshot.CaptureAsync(_svn, wc, dirty, null, ct)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        BackendLog.Warn("Backup delle modifiche pendenti prima del merge: " + ex.Message);
                        lock (_lock)
                            _error = ex is PatchBackup.NoSpaceException ns
                                ? Loc.T("Non c'è abbastanza spazio per la copia di sicurezza delle modifiche pendenti (servono {0}, liberi {1}): il merge non è partito e non è stato toccato nulla.",
                                    PatchBackup.FormatBytes(ns.Needed), PatchBackup.FormatBytes(ns.Free))
                                : Loc.T("Copia di sicurezza delle modifiche pendenti non riuscita ({0}): il merge non è partito e non è stato toccato nulla.", ex.Message);
                        Changed?.Invoke();
                        return;
                    }
                }

                // Sempre, anche senza modifiche pendenti (null): una copia di una working copy precedente non deve mai servire a questo annullamento.
                _changes.SetPreMerge(snapshot);
                var info = await TryInfoAsync(wc, ct);
                var state = new PendingMerge.Entry(source.Name, source.Url, source.Path, wc, _changes.MergeSubPath, info?.Url, info?.Revision,
                    revs.ToList(), [], PendingMerge.Running, DateTime.Now, DateTime.Now, Backup: snapshot?.Dir);
                _changes.PendingMerges.Set(_changes.WorkingCopyPath, state);
                await ExecuteAsync(source, state, snapshot, resumed: false, origin: "log", times, ct);
            });

            BackendLog.Info($"merge run: {Format(times)}, totale {total.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Fail(ex);
        }
        finally
        {
            Idle();
        }
    }

    private async Task<SvnInfo?> TryInfoAsync(string wc, CancellationToken ct)
    {
        try { return await _svn.GetInfoAsync(wc, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BackendLog.Warn("merge: svn info non letto: " + ex.Message.Split('\n')[0]);
            return null;
        }
    }

    // ── Continua merge ───────────────────────────────────────────────

    /// <summary>
    /// "Continua merge": applica solo le revisioni rimaste di un merge fermo o interrotto (conflitto, "Annulla" dell'utente, errore di rete, VS chiuso), senza rifare la scelta.
    /// Funziona anche con il Log non in modalita' merge (da Changes, dopo un riavvio): lo stato e' quello salvato su disco.
    /// </summary>
    internal async Task ResumeAsync(string origin, CancellationToken ct)
    {
        if (_changes.CurrentPendingMerge is not { } entry)
        {
            Report(Loc.T("Nessun merge da continuare."), err: false);
            return;
        }

        bool active;
        lock (_lock)
        {
            if (_busy)
                return;
            active = _active;
            if (active)
            {
                _busy = true;
                _error = string.Empty;
            }
        }

        if (active)
            Changed?.Invoke();
        try
        {
            await _changes.RunExclusiveAsync(async () =>
            {
                var times = new ConcurrentDictionary<string, long>();
                var source = new SwitchPickerStore.Target(entry.Name, string.Empty, entry.Url, false, entry.Path);
                var check = await CheckResumableAsync(entry, source, ct);
                if (check.Problem is { } problem)
                {
                    Report(problem, err: true);
                    _changes.SetStatusMessage(problem);
                    return;
                }

                var state = check.State!;
                if (state.Remaining.Count == 0)
                {
                    // Ormai tutto unito (mergeinfo): non c'e' altro da fare che committare.
                    _changes.PendingMerges.Clear(_changes.WorkingCopyPath);
                    await _changes.RefreshAsync(ct, reloadBranch: false, Loc.T("Merge completato: rivedi le modifiche e committa."));
                    var logs = await EnsureLogAsync(state.Revs, source, ct);
                    _changes.AfterMerge(MergeCommitMessage(state.Revs, logs, source.Path), Loc.T("Merge {0} da {1}: rivedi le modifiche e committa, oppure annulla il merge. Messaggio del merge disponibile nei Recenti.", RevsLabel(state.Revs), "^/" + source.Path));
                    return;
                }

                _changes.PendingMerges.Set(_changes.WorkingCopyPath, state);
                await ExecuteAsync(source, state, _changes.PreMerge, resumed: true, origin, times, ct);
            });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Fail(ex);
        }
        finally
        {
            if (active)
                Idle();
        }
    }

    /// <summary>Controlli prima di riprendere: conflitti ancora aperti, passaggio rimasto a meta' (cleanup), working copy cambiata (switch, update, revert) e revisioni davvero rimaste (mergeinfo).</summary>
    private async Task<(PendingMerge.Entry? State, string? Problem)> CheckResumableAsync(PendingMerge.Entry entry, SwitchPickerStore.Target source, CancellationToken ct)
    {
        var wc = entry.Wc;
        await _changes.RefreshAsync(ct, reloadBranch: true);
        if (entry.State == PendingMerge.Running || entry.InFlightFrom is not null)
        {
            var failed = await _changes.CleanupQuietlyAsync(wc, ct);
            if (failed is not null)
                return (null, Loc.T("Il cleanup della working copy non è riuscito ({0}): sistemalo (SVN Cleanup) e riprova.", failed));
        }

        var info = await TryInfoAsync(wc, ct);
        if (info is not null && entry.WcUrl is not null && !SameUrl(info.Url, entry.WcUrl))
            return (null, IncompatibleText(Loc.T("la working copy è su un altro ramo ({0}) rispetto a quando è iniziato il merge ({1})", info.Url, entry.WcUrl)));
        if (info is not null && entry.WcRev is { } rev0 && info.Revision != rev0)
            return (null, IncompatibleText(Loc.T("la working copy è stata aggiornata (da r{0} a r{1})", rev0, info.Revision)));
        var open = _changes.UnresolvedConflicts;
        if (open > 0)
            return (null, Loc.T("Restano {0} file in conflitto: risolvili e segnali come risolti, poi premi \"Continua merge\".", open));
        IReadOnlyList<long> eligible;
        try { eligible = await _svn.GetEligibleRevisionsAsync(wc, source.Url, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (null, Loc.T("Non riesco a leggere le revisioni ancora da unire ({0}): controlla la connessione e riprova.", ex.Message.Split('\n')[0]));
        }

        var back = entry.Applied.Except(entry.Unrecorded ?? []).Where(eligible.Contains).ToList();
        if (back.Count > 0)
            return (null, IncompatibleText(Loc.T("le revisioni {0} risultano di nuovo da unire (modifiche annullate fuori dal merge)", RevListText(back))));
        var remaining = entry.Revs.Where(eligible.Contains).Order().ToList();
        var applied = entry.Revs.Where(r => !remaining.Contains(r)).Order().ToList();
        return (entry with { Applied = applied, State = PendingMerge.Interrupted, InFlightFrom = null, InFlightTo = null }, null);
    }

    private string IncompatibleText(string why)
    {
        _changes.SetPendingMergeIncompatible(why);
        return Loc.T("Il merge interrotto non si può più continuare: {0}. Puoi solo annullarlo (\"Annulla merge\") o scartare lo stato (\"Scarta\").", why);
    }

    private static bool SameUrl(string? a, string? b)
        => a is not null && b is not null && string.Equals(Uri.UnescapeDataString(a).TrimEnd('/'), Uri.UnescapeDataString(b).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Elenco di revisioni per un messaggio: tutte fino a 6, poi la prima e l'ultima con il numero.</summary>
    internal static string RevListText(IReadOnlyList<long> revs)
    {
        var s = revs.Order().ToList();
        return s.Count <= 6 ? string.Join(", ", s.Select(r => $"r{r}"))
            : Loc.T("{0}, {1} … {2} ({3} revisioni)", $"r{s[0]}", $"r{s[1]}", $"r{s[^1]}", s.Count);
    }

    // ── Esecuzione (merge nuovo o ripreso) ───────────────────────────

    /// <summary>Applica le revisioni rimaste di <paramref name="state"/> e chiude nel modo giusto: concluso, fermo per conflitto, fermato dall'utente, interrotto da un errore.</summary>
    private async Task ExecuteAsync(SwitchPickerStore.Target source, PendingMerge.Entry state, PreMergeSnapshot? snapshot, bool resumed, string origin,
        ConcurrentDictionary<string, long> times, CancellationToken ct)
    {
        var root = _changes.WorkingCopyPath;
        var wc = state.Wc;
        var revs = state.Revs.Order().ToList();
        var position = revs.Select((r, i) => (r, i)).ToDictionary(x => x.r, x => x.i + 1);
        var label = RevsLabel(revs);
        var cur = state;
        void Persist() => _changes.PendingMerges.Set(root, cur);
        void ShowStep(long first, long last, int done)
            => _changes.Progress.SetStep(new MergeStepDto(first == last ? $"r{first}" : $"r{first}-r{last}", position.GetValueOrDefault(first), position.GetValueOrDefault(last), done, revs.Count));

        Say(Loc.T("Merge da {0} in corso...", source.Name));
        var ot = _changes.Progress.Begin("merge", Loc.T("Merge da {0}", source.Name), resumed ? Loc.T("Continuo il merge...") : Loc.T("Merge in corso..."), ct, origin: origin, softCancel: true);
        var checking = false;
        var runner = new MergeRunner(_svn, wc, source.Url, (rs, c) => TouchedAsync(rs, source, c))
        {
            Phase = text => { checking = true; _changes.Progress.SetStep(null); _changes.Progress.SetPhase(text); },
            StopRequested = () => _changes.Progress.SoftCancelRequested,
        };
        runner.StepStarting = step =>
        {
            if (checking)
            {
                checking = false;
                _changes.Progress.SetPhase(resumed ? Loc.T("Continuo il merge...") : Loc.T("Merge in corso..."));
            }

            cur = cur with { State = PendingMerge.Running, InFlightFrom = step.First, InFlightTo = step.Last };
            Persist();
            ShowStep(step.First, step.Last, position.GetValueOrDefault(step.First) - 1);
        };
        runner.StepDone = applied =>
        {
            cur = cur with { Applied = cur.Applied.Union(applied).Order().ToList(), InFlightFrom = null, InFlightTo = null };
            Persist();
            ShowStep(applied[0], applied[^1], cur.Applied.Count);
        };
        // Passate per revisione dentro un solo processo (-c 5,6,7): le intestazioni "--- Merging r6 into" dicono a che revisione si e' arrivati.
        void Sink(string raw)
        {
            _changes.Progress.Line(raw);
            if (MergeHeader.TryParse(raw, out var first, out var last) && position.ContainsKey(first) && position.ContainsKey(last))
                ShowStep(first, last, position[first] - 1);
        }

        MergeRunResult result;
        try
        {
            var eligible = Active ? Entries.Select(e => e.Rev).ToList() : (await _svn.GetEligibleRevisionsAsync(wc, source.Url, ot)).ToList();
            var remaining = cur.Remaining;
            result = await Timed(times, "svn merge", () => SvnProgress.RunAsync(Sink, () => runner.RunAsync(remaining, eligible, ot)));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && _changes.Progress.IsCancelling)
        {
            // Terminato a forza (il passaggio non finiva): il passaggio in corso puo' essere a meta'.
            cur = cur with { InFlightFrom = runner.InFlight?.First ?? cur.InFlightFrom, InFlightTo = runner.InFlight?.Last ?? cur.InFlightTo };
            await InterruptAsync(source, cur, snapshot, revs, cleanup: true, hard: true, reason: null, ct);
            return;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BackendLog.Warn("merge fallito: " + ex.Message);
            await FailedAsync(source, cur, snapshot, resumed, ex, ct);
            return;
        }

        if (result.Error is { } error)
        {
            BackendLog.Warn("merge fallito: " + error.Message);
            await FailedAsync(source, cur, snapshot, resumed, error, ct);
            return;
        }

        if (result.StoppedByUser)
        {
            await InterruptAsync(source, cur, snapshot, revs, cleanup: false, hard: false, reason: null, ct);
            return;
        }

        var summary = ToResult("merge", label, result.Output);
        lock (_lock)
            _result = summary;
        if (result.Stopped)
        {
            await StoppedAsync(source, cur, snapshot, revs, result, ct);
            return;
        }

        // Concluso: tutte le revisioni applicate. Lo stato su disco non serve piu' (resta la copia di sicurezza fino al commit o all'annullamento).
        _changes.PendingMerges.Clear(root);
        var logs = await EnsureLogAsync(revs, source, ct);
        Task? reload = Active ? ReloadEligibleAsync(ct, times) : null;
        var conflictsNow = result.Conflicts;
        await TimedRun(times, "refresh", () => _changes.RefreshAsync(ct, reloadBranch: false, conflictsNow > 0
            ? Loc.T("Merge completato: {0} file in conflitto da risolvere.", conflictsNow)
            : Loc.T("Merge completato: rivedi le modifiche e committa.")));
        // Dopo il refresh: il repository (chiave dei Recenti) e' noto.
        _changes.AfterMerge(MergeCommitMessage(revs, logs, source.Path),
            Loc.T("Merge {0} da {1}: rivedi le modifiche e committa, oppure annulla il merge. Messaggio del merge disponibile nei Recenti.", label, "^/" + source.Path)
            + (snapshot is null ? string.Empty : " " + Loc.T("Le tue {0} modifiche pendenti sono nella working copy insieme al merge (finiscono nello stesso commit se non scegli i file): \"Annulla merge\" le ripristina.", snapshot.Entries.Count)));
        _changes.Progress.Done(true, conflictsNow > 0
            ? Loc.T("Merge completato con {0} conflitti: vanno risolti prima di committare.", conflictsNow)
            : Loc.T("Merge completato: rivedi le modifiche e committa."), conflictsNow);
        if (reload is not null)
            await reload;
        lock (_lock)
            _selected.Clear();
        RequestFiles();
        BackendLog.Info($"merge: {Format(times)}, {result.Items.Count} file, {result.Conflicts} conflitti");
        Say(conflictsNow > 0
            ? Loc.T("Merge eseguito, da committare. Conflitti da risolvere in Changes: {0}.", conflictsNow)
            : Loc.T("Merge eseguito, da committare."));
        // I conflitti li apre il pulsante della finestra di avanzamento (niente salto di finestra mentre e' aperta).
    }

    /// <summary>Stato da salvare dopo uno stop: applicate = quelle che il runner sa piu' quelle che mergeinfo non da' piu' da unire (un processo terminato a forza puo' aver registrato qualcosa).</summary>
    private async Task<PendingMerge.Entry> FreezeAsync(SwitchPickerStore.Target source, PendingMerge.Entry cur, string state, string? reason, CancellationToken ct)
    {
        IReadOnlyList<long>? eligible = null;
        try { eligible = await _svn.GetEligibleRevisionsAsync(cur.Wc, source.Url, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { BackendLog.Warn("merge: mergeinfo non letto allo stop: " + ex.Message.Split('\n')[0]); }
        var applied = cur.Applied.ToList();
        if (eligible is not null)
            applied.AddRange(cur.Revs.Where(r => !eligible.Contains(r)));
        applied = applied.Distinct().Order().ToList();
        var unrecorded = eligible is null ? [] : applied.Where(eligible.Contains).ToList();
        return cur with { Applied = applied, State = state, Reason = reason, Unrecorded = unrecorded };
    }

    /// <summary>Fermo dopo la revisione in conflitto: le successive non sono state applicate. Stato salvato, banner in Changes con Continua/Annulla.</summary>
    private async Task StoppedAsync(SwitchPickerStore.Target source, PendingMerge.Entry cur, PreMergeSnapshot? snapshot, IReadOnlyList<long> revs, MergeRunResult result, CancellationToken ct)
    {
        var root = _changes.WorkingCopyPath;
        BackendLog.Info($"merge fermato da un conflitto alla r{result.ConflictRev}");
        cur = await FreezeAsync(source, cur, PendingMerge.Stopped, null, ct);
        cur = cur with
        {
            ConflictRev = result.ConflictRev,
            ConflictFrom = result.ConflictRange?.First,
            ConflictTo = result.ConflictRange?.Last,
        };
        if (cur.Remaining.Count == 0)
        {
            // La revisione in conflitto era l'ultima: il merge e' completo, restano solo i conflitti da risolvere (come un merge normale).
            _changes.PendingMerges.Clear(root);
            var logs = await EnsureLogAsync(revs, source, ct);
            await _changes.RefreshAsync(ct, reloadBranch: false, Loc.T("Merge completato: {0} file in conflitto da risolvere.", result.Conflicts));
            _changes.AfterMerge(MergeCommitMessage(revs, logs, source.Path),
                Loc.T("Merge {0} da {1}: rivedi le modifiche e committa, oppure annulla il merge. Messaggio del merge disponibile nei Recenti.", RevsLabel(revs), "^/" + source.Path));
            _changes.Progress.Done(true, Loc.T("Merge completato con {0} conflitti: vanno risolti prima di committare.", result.Conflicts), result.Conflicts);
            await AfterStopAsync(ct);
            Say(Loc.T("Merge eseguito, da committare. Conflitti da risolvere in Changes: {0}.", result.Conflicts));
            return;
        }

        _changes.PendingMerges.Set(root, cur);
        await _changes.RefreshAsync(ct, reloadBranch: false, Loc.T("Merge fermato da un conflitto: risolvi i conflitti qui sotto, poi \"Continua merge\"."));
        cur = cur with { ConflictFiles = _changes.UnresolvedConflicts };
        _changes.PendingMerges.Set(root, cur);
        var done = cur.Applied.Count;
        _changes.Progress.Done(false, result.ConflictRev is { } r
            ? Loc.T("Merge fermo alla r{0} per conflitto: applicate {1} di {2}, rimaste {3}. Cosa fare è scritto in Changes.", r, done, revs.Count, cur.Remaining.Count)
            : Loc.T("Merge fermo per conflitto nel gruppo r{0}-r{1}: applicate {2} di {3}, rimaste {4}. Cosa fare è scritto in Changes.", result.ConflictRange?.First ?? 0, result.ConflictRange?.Last ?? 0, done, revs.Count, cur.Remaining.Count), _changes.UnresolvedConflicts);
        await AfterStopAsync(ct);
    }

    private async Task AfterStopAsync(CancellationToken ct)
    {
        if (Active)
            await ReloadEligibleAsync(ct); // le applicate non sono piu' da unire, le rimaste si
        lock (_lock)
            _selected.Clear();
        RequestFiles();
        Changed?.Invoke();
    }

    /// <summary>Fermato dall'utente ("Annulla" nella finestra di avanzamento) o terminato a forza: nulla di applicato = niente da continuare; altrimenti stato "interrotto".</summary>
    private async Task InterruptAsync(SwitchPickerStore.Target source, PendingMerge.Entry cur, PreMergeSnapshot? snapshot, IReadOnlyList<long> revs, bool cleanup, bool hard, string? reason, CancellationToken ct)
    {
        var root = _changes.WorkingCopyPath;
        string? cleanupFailed = null;
        if (cleanup)
        {
            _changes.Progress.SetPhase(Loc.T("Cleanup in corso..."));
            cleanupFailed = await _changes.CleanupQuietlyAsync(cur.Wc, ct);
        }

        cur = await FreezeAsync(source, cur, PendingMerge.Interrupted, reason, ct);
        if (!hard)
            cur = cur with { InFlightFrom = null, InFlightTo = null };
        if (cur.Applied.Count == 0 && cur.InFlightFrom is null)
        {
            // Nessuna revisione applicata: non c'e' un merge a meta'.
            _changes.PendingMerges.Clear(root);
            snapshot?.Delete();
            _changes.SetPreMerge(null);
            await _changes.RefreshAsync(ct, reloadBranch: false, Loc.T("Merge annullato prima di applicare revisioni: la working copy non è stata toccata."));
            _changes.Progress.Done(false, Loc.T("Merge annullato prima di applicare revisioni: la working copy non è stata toccata."), cancelled: true);
            await AfterStopAsync(ct);
            return;
        }

        _changes.PendingMerges.Set(root, cur);
        await _changes.RefreshAsync(ct, reloadBranch: false, Loc.T("Merge interrotto: applicate {0} di {1} revisioni. Usa \"Continua merge\" per applicare le rimaste.", cur.Applied.Count, revs.Count));
        var why = hard ? Loc.T("Merge terminato a forza dopo {0} di {1} revisioni applicate.", cur.Applied.Count, revs.Count)
            : Loc.T("Merge fermato su tua richiesta dopo {0} di {1} revisioni applicate: nulla è rimasto a metà.", cur.Applied.Count, revs.Count);
        _changes.Progress.Done(false, why + " " + Loc.T("Rimaste {0}.", cur.Remaining.Count) + (cleanupFailed is null ? string.Empty : " " + Loc.T("Il cleanup automatico non è riuscito: {0}", cleanupFailed)), cancelled: true,
            actions: [new(Loc.T("Continua merge"), "mergeContinue"), new(Loc.T("Annulla merge"), "progressRevertMerge"), new(Loc.T("Lascia così"), "progressClose")]);
        await AfterStopAsync(ct);
    }

    /// <summary>Errore di svn (rete, permessi...) durante il merge. Prima di aver applicato qualcosa e' un errore e basta (come sempre); dopo, il merge diventa "interrotto" e si puo' continuare.</summary>
    private async Task FailedAsync(SwitchPickerStore.Target source, PendingMerge.Entry cur, PreMergeSnapshot? snapshot, bool resumed, Exception ex, CancellationToken ct)
    {
        var root = _changes.WorkingCopyPath;
        Fail(ex);
        var inFlight = cur.InFlightFrom is not null;
        var frozen = await FreezeAsync(source, cur, PendingMerge.Interrupted, ex.Message.Split('\n')[0], ct);
        if (frozen.Applied.Count == 0 && !resumed)
        {
            _changes.PendingMerges.Clear(root);
            _changes.Progress.Done(false, Loc.T("Errore: {0}", ex.Message));
            await _changes.RefreshAsync(ct, reloadBranch: false); // un merge fallito a meta' puo' aver toccato dei file
            if (snapshot is not null)
                // Le modifiche pendenti sono mescolate a quelle di un merge a meta': si lascia "Annulla merge", che le ripristina dalla copia di sicurezza.
                _changes.AfterMerge(string.Empty, Loc.T("Il merge non è andato a buon fine e può aver modificato dei file. Per tornare com'eri prima annulla il merge: le tue {0} modifiche pendenti vengono ripristinate dalla copia di sicurezza.", snapshot.Entries.Count));
            else
                _changes.SetPreMerge(null);
            return;
        }

        if (inFlight)
        {
            var failed = await _changes.CleanupQuietlyAsync(cur.Wc, ct);
            if (failed is not null)
                BackendLog.Warn("merge: cleanup dopo l'errore non riuscito: " + failed);
        }

        _changes.PendingMerges.Set(root, frozen);
        await _changes.RefreshAsync(ct, reloadBranch: false);
        _changes.Progress.Done(false, Loc.T("Errore: {0}", ex.Message) + " " + Loc.T("Merge interrotto dopo {0} di {1} revisioni applicate: puoi continuarlo.", frozen.Applied.Count, frozen.Revs.Count));
        await AfterStopAsync(ct);
    }

    /// <summary>File toccati da ogni revisione, relativi alla cartella del merge, per trovare chi tocca i file in conflitto previsti dal dry-run.</summary>
    private async Task<IReadOnlyDictionary<long, IReadOnlySet<string>>> TouchedAsync(IReadOnlyList<long> revs, SwitchPickerStore.Target source, CancellationToken ct)
    {
        await EnsurePathsAsync(revs, source, ct);
        var map = new Dictionary<long, IReadOnlySet<string>>();
        lock (_lock)
            foreach (var r in revs)
                map[r] = (_paths.GetValueOrDefault(r) ?? []).Select(p => MergeFiles.Relative(p.Path, source.Path)).Where(p => p is not null).Select(p => p!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return map;
    }

    /// <summary>Autore, data e messaggio delle revisioni indicate (per il messaggio di commit): dalla cache, il resto da <c>svn log</c> a gruppi.</summary>
    private async Task<IReadOnlyList<LogEntryDto>> EnsureLogAsync(IReadOnlyList<long> revs, SwitchPickerStore.Target source, CancellationToken ct)
    {
        List<long> missing;
        lock (_lock)
            missing = revs.Where(r => !_logCache.ContainsKey(r)).Order().ToList();
        if (missing.Count > 0)
        {
            try
            {
                var batches = await Task.WhenAll(GroupRevisions(missing).Select(g => _svn.GetLogRangeAsync(source.Url, g.Min, g.Max, ct, withPaths: false)));
                lock (_lock)
                {
                    var wanted = missing.ToHashSet();
                    foreach (var e in batches.SelectMany(b => b).Where(e => wanted.Contains(e.Revision)))
                        _logCache[e.Revision] = LogStore.ToDto(e);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                BackendLog.Warn("merge: log delle revisioni non letto: " + ex.Message.Split('\n')[0]);
            }
        }

        lock (_lock)
            return revs.Select(r => _logCache.TryGetValue(r, out var d) ? d : new LogEntryDto(r, string.Empty, string.Empty, string.Empty, [])).ToList();
    }

    /// <summary>Elenco per revisione (dalla piu' vecchia, l'ordine in cui il merge le applica) con i conflitti previsti dal dry-run.</summary>
    private IReadOnlyList<MergeRevFilesDto> ByRevision(IReadOnlyList<long> revs, MergeOutput dryRun)
    {
        var conflicts = MergeFiles.ConflictPaths(dryRun);
        lock (_lock)
        {
            var sourcePath = _source!.Path;
            return revs.Select(r =>
            {
                var files = MergeFiles.ForRevision(_paths.GetValueOrDefault(r) ?? [], sourcePath, conflicts);
                var (items, more) = MergeFiles.Cap(files, MergeFiles.MaxPerRevision);
                var e = _logCache.GetValueOrDefault(r);
                return new MergeRevFilesDto(r, e?.Author ?? string.Empty, (e?.Msg ?? string.Empty).Split('\n')[0], items, more, files.Count(f => f.C));
            }).ToList();
        }
    }

    /// <summary>File con modifiche pendenti che il merge tocca (dal dry-run): a rischio di conflitto. Percorsi relativi con <c>/</c>.</summary>
    internal static List<string> Overlap(IReadOnlyList<SvnFileStatus> pending, MergeOutput dryRun)
    {
        static string Norm(string p)
        {
            p = p.Replace('\\', '/');
            return p.StartsWith("./", StringComparison.Ordinal) ? p[2..] : p;
        }

        var touched = dryRun.Items.Select(i => Norm(i.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return pending.Select(p => Norm(p.Path)).Where(touched.Contains).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>File in comune con le modifiche pendenti dal log (<c>svn log -v</c> in cache): per quando il dry-run non e' utilizzabile.</summary>
    private async Task<List<string>> OverlapFromLogAsync(IReadOnlyList<SvnFileStatus> pending, long[]? revs, CancellationToken ct)
    {
        List<long> all;
        string sourcePath;
        lock (_lock)
        {
            all = revs?.ToList() ?? _entries.Select(e => e.Rev).Order().ToList();
            sourcePath = _source!.Path;
        }

        await EnsurePathsAsync(all, ct);
        HashSet<string> touched;
        lock (_lock)
            touched = all.SelectMany(r => _paths.GetValueOrDefault(r) ?? []).Select(p => MergeFiles.Relative(p.Path, sourcePath)).Where(p => p is not null).Select(p => p!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return pending.Select(p => p.Path.Replace('\\', '/')).Select(p => p.StartsWith("./", StringComparison.Ordinal) ? p[2..] : p).Where(touched.Contains).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Avviso prima di un merge su una working copy con modifiche pendenti. <paramref name="overlapKnown"/> falso = non si e' potuto prevedere quali file il merge tocca.</summary>
    internal static string PendingMergeText(int pendingCount, IReadOnlyList<string> overlap, bool overlapKnown = true)
    {
        var text = Loc.T("La working copy ha {0} elementi con modifiche non committate.\n\nIl merge verrà fatto sopra le tue modifiche. Prima ne salvo una copia di sicurezza (cartella .svn-assist\\backups della working copy): \"Annulla merge\" le ripristinerà com'erano. Se poi committi, finiscono nello stesso commit del merge (a meno che tu scelga solo alcuni file).", pendingCount);
        if (overlap.Count > 0)
            text += "\n\n" + Loc.T("ATTENZIONE: il merge tocca {0} file che hai già modificato, quindi c'è rischio di conflitti: {1}", overlap.Count,
                string.Join(", ", overlap.Take(8)) + (overlap.Count > 8 ? Loc.T(" e altri {0}", overlap.Count - 8) : string.Empty));
        if (!overlapKnown)
            text += "\n\n" + Loc.T("Non ho potuto prevedere quali file il merge tocca: se ne hai modificati in comune possono esserci conflitti.");
        return text + "\n\n" + Loc.T("Unire comunque?");
    }

    private bool TryBusy(out SwitchPickerStore.Target source, out string wc, out long[]? revs, bool forMerge = false)
    {
        lock (_lock)
        {
            source = _source!;
            wc = _wc;
            revs = _selected.Count > 0 ? _selected.Order().ToArray() : null;
            if (_busy || _loading || !_active || _source is null)
                return false;
            _busy = true;
            _error = string.Empty;
            if (!forMerge)
                _result = null;
        }

        Changed?.Invoke();
        return true;
    }

    private void Idle()
    {
        lock (_lock)
            _busy = false;
        Changed?.Invoke();
    }

    /// <summary>Esito di un'azione fuori dal merge (es. aprire il diff di un file): messaggio della barra di stato o errore del pannello.</summary>
    internal void Report(string text, bool err)
    {
        lock (_lock)
        {
            if (err)
                _error = text;
            else
            {
                _error = string.Empty;
                _message = text;
            }
        }

        Changed?.Invoke();
    }

    private void Say(string text)
    {
        lock (_lock)
            _message = text;
        Changed?.Invoke();
    }

    private void Fail(Exception ex)
    {
        lock (_lock)
            _error = Loc.T("Errore: {0}", ex.Message);
        Changed?.Invoke();
    }

    /// <summary>Tutto quello che e' da unire, come intervallo: <c>r120-r135</c> (<c>r120</c> se e' una sola).</summary>
    private string AllLabel()
    {
        long first, last;
        lock (_lock)
        {
            if (_entries.Count == 0)
                return string.Empty;
            (first, last) = (_entries.Min(e => e.Rev), _entries.Max(e => e.Rev));
        }

        return first == last ? $"r{first}" : $"r{first}-r{last}";
    }

    /// <summary>Intervalli come TortoiseSVN: <c>1,3-5</c> (revisioni consecutive unite in un intervallo).</summary>
    internal static string RevRanges(IEnumerable<long> revs) => MergeParser.RevisionRanges(revs);

    /// <summary>
    /// Messaggio di commit dopo un merge, nel formato di TortoiseSVN: <c>Merged revision(s) 4-6 from branches/x:</c> e, per ogni revisione,
    /// una riga <c>........</c> seguita da <c>rN | autore | messaggio originale</c>. In italiano <c>Unite le revisioni 4-6 da branches/x:</c>.
    /// Una revisione senza dettagli (il log non l'ha restituita) resta solo <c>rN</c>.
    /// </summary>
    internal static string MergeCommitMessage(IReadOnlyList<long> revs, IReadOnlyList<LogEntryDto> entries, string sourcePath)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Loc.T("Unite le revisioni {0} da {1}:", RevRanges(revs), sourcePath));
        foreach (var r in revs.Distinct().Order())
        {
            sb.Append("\n........\n").Append('r').Append(r);
            if (entries.FirstOrDefault(e => e.Rev == r) is { } e && (e.Author.Length > 0 || e.Msg.Trim().Length > 0))
                sb.Append(" | ").Append(e.Author).Append(" | ").Append(e.Msg.Trim());
        }

        return sb.ToString();
    }

    /// <summary>Revisioni scelte per il messaggio di commit: <c>r120, r124</c>; oltre 6, l'intervallo con il numero di revisioni scelte.</summary>
    internal static string RevsLabel(IReadOnlyList<long> revs)
    {
        var sorted = revs.Order().ToList();
        return sorted.Count <= 6 ? string.Join(", ", sorted.Select(r => $"r{r}"))
            : Loc.T("{0} (selezione di {1} revisioni)", $"r{sorted[0]}-r{sorted[^1]}", sorted.Count);
    }

    /// <summary>Distanza massima (in numeri di revisione) tra due revisioni da unire perche' stiano nella stessa richiesta di log.</summary>
    internal const long LogGap = 500;
    private const int LogParallelism = 4;

    /// <summary>
    /// Gruppi di revisioni vicine: un <c>svn log -r max:min</c> per gruppo (un giro al server ciascuno). Una revisione isolata e lontana
    /// non fa leggere migliaia di revisioni in mezzo; revisioni vicine restano in una richiesta sola, senza una chiamata per revisione.
    /// </summary>
    internal static List<(long Min, long Max)> GroupRevisions(IEnumerable<long> sortedRevs, long gap = LogGap)
    {
        var groups = new List<(long Min, long Max)>();
        foreach (var r in sortedRevs)
        {
            if (groups.Count > 0 && r - groups[^1].Max <= gap)
                groups[^1] = (groups[^1].Min, r);
            else
                groups.Add((r, r));
        }

        return groups;
    }

    /// <summary>
    /// Elenca le revisioni del sorgente ancora da unire con autore, data e messaggio. Il log si chiede <b>senza <c>-v</c></b> (i file toccati sono
    /// la parte pesante: si leggono solo per le revisioni scelte), solo per le revisioni non gia' in cache, a gruppi e in parallelo.
    /// </summary>
    private async Task ReloadEligibleAsync(CancellationToken ct, ConcurrentDictionary<string, long>? times = null)
    {
        times ??= new();
        string wc;
        SwitchPickerStore.Target source;
        lock (_lock)
        {
            wc = _wc;
            source = _source!;
        }

        var eligible = await Timed(times, "eligible", () => _svn.GetEligibleRevisionsAsync(wc, source.Url, ct));
        List<long> missing;
        lock (_lock)
            missing = eligible.Where(r => !_logCache.ContainsKey(r)).ToList();
        if (missing.Count > 0)
        {
            var sw = Stopwatch.StartNew();
            var groups = GroupRevisions(missing);
            using var gate = new SemaphoreSlim(LogParallelism);
            var batches = await Task.WhenAll(groups.Select(async g =>
            {
                await gate.WaitAsync(ct);
                try { return await _svn.GetLogRangeAsync(source.Url, g.Min, g.Max, ct, withPaths: false); }
                finally { gate.Release(); }
            }));
            lock (_lock)
            {
                var wanted = missing.ToHashSet();
                foreach (var e in batches.SelectMany(b => b).Where(e => wanted.Contains(e.Revision)))
                    _logCache[e.Revision] = LogStore.ToDto(e);
            }

            times.AddOrUpdate("log", sw.ElapsedMilliseconds, (_, old) => old + sw.ElapsedMilliseconds);
            times.AddOrUpdate("logCalls", groups.Count, (_, old) => old + groups.Count);
        }

        // Una revisione che il log non restituisce (permessi) resta comunque unibile: riga senza dettagli.
        List<LogEntryDto> entries;
        lock (_lock)
            entries = eligible.OrderByDescending(r => r)
                .Select(r => _logCache.TryGetValue(r, out var d) ? d : new LogEntryDto(r, string.Empty, string.Empty, string.Empty, [])).ToList();
        lock (_lock)
        {
            _entries = entries;
            _selected.IntersectWith(entries.Select(e => e.Rev));
            _message = entries.Count == 0 ? Loc.T("Nessuna revisione da unire: {0} è già allineato.", source.Name) : Loc.T("{0} revisioni da unire.", entries.Count);
        }

        RequestFiles(); // la scelta puo' essere cambiata (revisioni sparite dall'elenco)
    }

    /// <summary>Working copy con modifiche locali ai file versionati? Aggiorna <c>Blocked</c> e ritorna <c>true</c> se il merge e' da rifiutare.</summary>
    private async Task<bool> CheckBlockedAsync(CancellationToken ct)
    {
        string wc;
        lock (_lock)
            wc = _wc;
        var changes = await _svn.GetLocalChangesAsync(wc, ct);
        // Un elemento aggiunto con storia (svn copy/move, o file aggiunto da un merge non ancora committato) dopo un revert e una patch tornerebbe "aggiunto" senza storia: si blocca.
        var hard = changes.Count(s => PreMergeSnapshot.IsBlocking(s.Status) || (PreMergeSnapshot.IsPending(s.Status) && s.IsCopied));
        var pending = changes.Where(s => PreMergeSnapshot.IsPending(s.Status) && !s.IsCopied).ToList();
        lock (_lock)
        {
            // Le modifiche pendenti (M, A, D, R) non bloccano: il merge chiede conferma e salva prima una copia di sicurezza che "Annulla merge" ripristina.
            // Blocca cio' che svn non ripristina in modo affidabile dopo un revert: conflitti, file mancanti, elementi a meta' o al posto di altri.
            _pending = pending;
            _blocked = hard == 0 ? string.Empty
                : Loc.T("La working copy ha {0} elementi in conflitto, mancanti, a metà o aggiunti con storia (svn copy/move, o file aggiunti da un merge non ancora committato): dopo un merge non si possono ripristinare in modo affidabile. Risolvili o committali (Changes) oppure fai Revert, poi riprova.", hard);
            return _blocked.Length > 0;
        }
    }

    internal static MergeResultDto ToResult(string mode, string revs, MergeOutput o)
    {
        static bool Is(MergeItem i, char c) => !i.Conflicted && (i.File == c || (i.File == ' ' && i.Prop == c));
        var counts = new MergeCountsDto(o.Items.Count(i => Is(i, 'U')), o.Items.Count(i => Is(i, 'A')), o.Items.Count(i => Is(i, 'D')),
            o.Items.Count(i => Is(i, 'G')), o.Conflicts);
        return new MergeResultDto(mode, revs, counts, o.Items.Select(i => new LogPathDto(i.Action, i.Path, "file")).ToList(), o.Skipped);
    }
}
