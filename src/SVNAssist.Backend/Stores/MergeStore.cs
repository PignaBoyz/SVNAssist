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

    internal string Title(SwitchPickerStore.Target source) => Loc.T("Merge da {0}", source.Name);

    /// <summary>"Risolvi i conflitti": porta in primo piano Changes, dove c'e' la sezione Conflitti.</summary>
    internal Task ShowConflictsAsync() => _changes.ShowWindowAsync();

    internal MergeDto? GetSnapshot()
    {
        var pending = _changes.MergePending; // prima del lock: prende il lock di ChangesStore
        lock (_lock)
            return !_active || _source is null ? null
                : new MergeDto(_source.Name, Title(_source), _loading, _busy, _error, _blocked, _selected.Order().ToList(), _entries.Count, _result, pending, SvnErrors.SuggestsUpdate(_error), _filesDto);
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
    private async Task EnsurePathsAsync(IReadOnlyList<long> revs, CancellationToken ct)
    {
        List<long> missing;
        Dictionary<long, int> index;
        SwitchPickerStore.Target source;
        lock (_lock)
        {
            source = _source!;
            missing = revs.Where(r => !_paths.ContainsKey(r)).Distinct().ToList();
            index = [];
            for (var i = 0; i < _entries.Count; i++)
                index[_entries[i].Rev] = i;
        }

        if (missing.Count == 0)
            return;
        var sw = Stopwatch.StartNew();
        var runs = new List<(long Min, long Max)>();
        int? prev = null;
        foreach (var r in missing.OrderBy(r => index.GetValueOrDefault(r, int.MaxValue)))
        {
            var i = index.GetValueOrDefault(r, int.MaxValue);
            if (prev is { } p && i == p + 1)
                runs[^1] = (Math.Min(runs[^1].Min, r), Math.Max(runs[^1].Max, r));
            else
                runs.Add((r, r));
            prev = i;
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
            await Task.WhenAll(blocked, merge, paths).ConfigureAwait(false);
            var output = await merge;
            var total = revs?.Length ?? Entries.Count;
            var result = ToResult("preview", revs is null ? AllLabel() : RevsLabel(revs), output) with { ByRev = ByRevision(shown, output), ByRevMore = Math.Max(0, total - shown.Count) };
            lock (_lock)
                _result = result;
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

    /// <summary>Merge vero: tutto quello che e' da unire (<paramref name="all"/>) oppure il cherry-pick delle revisioni scelte.</summary>
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

        if (!TryBusy(out var source, out var wc, out var picked, forMerge: true))
            return;
        try
        {
            var revs = all ? null : picked;
            var total = Stopwatch.StartNew();
            var times = new ConcurrentDictionary<string, long>();
            // Il merge richiede una working copy pulita: lo si ricontrolla sempre, e' cambiata dall'ultima volta.
            if (await Timed(times, "status", () => CheckBlockedAsync(ct)))
                return;

            var label = revs is null ? AllLabel() : RevsLabel(revs);
            MergeOutput? output = null;
            Task? reload = null;
            var stopped = false;
            await _changes.RunExclusiveAsync(async () =>
            {
                try
                {
                    Say(Loc.T("Merge da {0} in corso...", source.Name));
                    var ot = _changes.Progress.Begin("merge", Loc.T("Merge da {0}", source.Name), Loc.T("Merge in corso..."), ct);
                    output = await Timed(times, "svn merge", () => SvnProgress.RunAsync(_changes.Progress.Line, () => _svn.MergeAsync(wc, source.Url, revs, dryRun: false, ot)));
                    // Le revisioni ancora da unire si rileggono (mergeinfo) mentre Changes rilegge lo stato: due letture indipendenti.
                    IReadOnlyList<LogEntryDto> shown;
                    lock (_lock)
                        shown = _entries; // prima della rilettura: il messaggio di commit descrive le revisioni appena unite
                    reload = ReloadEligibleAsync(ct, times);
                    var summary = ToResult("merge", label, output);
                    lock (_lock)
                        _result = summary;
                    await TimedRun(times, "refresh", () => _changes.RefreshAsync(ct, reloadBranch: false, output.Conflicts > 0
                        ? Loc.T("Merge completato: {0} file in conflitto da risolvere.", output.Conflicts)
                        : Loc.T("Merge completato: rivedi le modifiche e committa.")));
                    // Dopo il refresh: il repository (chiave dei Recenti) e' noto.
                    _changes.AfterMerge(MergeCommitMessage(revs is null ? shown.Select(e => e.Rev).ToList() : revs, shown, source.Path),
                        Loc.T("Merge {0} da {1}: rivedi le modifiche e committa, oppure annulla il merge. Messaggio del merge disponibile nei Recenti.", label, "^/" + source.Path));
                    _changes.Progress.Done(true, output.Conflicts > 0
                        ? Loc.T("Merge completato con {0} conflitti: vanno risolti prima di committare.", output.Conflicts)
                        : Loc.T("Merge completato: rivedi le modifiche e committa."), output.Conflicts);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && _changes.Progress.IsCancelling)
                {
                    await _changes.FinishMergeCancelledAsync(wc, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && SvnErrors.IsMergeStoppedByConflict(ex.Message))
                {
                    stopped = true;
                    await MergeStoppedAsync(ex, source, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    BackendLog.Warn("merge fallito: " + ex.Message);
                    Fail(ex);
                    _changes.Progress.Done(false, Loc.T("Errore: {0}", ex.Message));
                    await _changes.RefreshAsync(ct, reloadBranch: false); // un merge fallito a meta' puo' aver toccato dei file
                }
            });

            if (stopped)
            {
                await ReloadEligibleAsync(ct); // le revisioni gia' applicate non sono piu' da unire, quelle dopo il conflitto si
                BackendLog.Info($"merge fermato da un conflitto: {Format(times)}, totale {total.ElapsedMilliseconds} ms");
            }

            if (output is not null)
            {
                if (reload is not null)
                    await reload;
                lock (_lock)
                    _selected.Clear();
                RequestFiles();
                BackendLog.Info($"merge run: {Format(times)}, totale {total.ElapsedMilliseconds} ms, {output.Items.Count} file, {output.Conflicts} conflitti");
                Say(output.Conflicts > 0
                    ? Loc.T("Merge eseguito, da committare. Conflitti da risolvere in Changes: {0}.", output.Conflicts)
                    : Loc.T("Merge eseguito, da committare."));
                // I conflitti li apre il pulsante della finestra di avanzamento (niente salto di finestra mentre e' aperta).
            }
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

    /// <summary>
    /// <c>svn merge</c> si e' fermato alla prima revisione in conflitto (E155015): quelle prima sono applicate, quella ha i conflitti, le successive no.
    /// Non e' un errore da mostrare in inglese e tre volte: Changes si aggiorna con la sezione Conflitti, "Annulla merge" resta disponibile
    /// e la spiegazione (in italiano, con cosa fare) compare una volta sola, nel pannello.
    /// </summary>
    private async Task MergeStoppedAsync(Exception ex, SwitchPickerStore.Target source, CancellationToken ct)
    {
        BackendLog.Warn("merge fermato da un conflitto: " + ex.Message);
        var rev = SvnErrors.ConflictingRevision(ex.Message);
        var why = rev is { } r
            ? Loc.T("Il merge si è fermato alla revisione r{0}, che è andata in conflitto: le revisioni successive non sono state applicate.", r)
            : Loc.T("Il merge si è fermato alla prima revisione in conflitto: le revisioni successive non sono state applicate.");
        var todo = Loc.T("Risolvi i conflitti in Changes, poi rifai il merge delle revisioni rimaste (restano in elenco), oppure annulla il merge.");
        lock (_lock)
        {
            _error = why + "\n" + todo;
            _result = null;
        }

        await _changes.RefreshAsync(ct, reloadBranch: false, Loc.T("Merge fermato da un conflitto: risolvi i conflitti qui sotto."));
        _changes.AfterMerge(string.Empty, Loc.T("Merge da {0} fermato da un conflitto: risolvi i conflitti, poi committa oppure annulla il merge.", "^/" + source.Path));
        _changes.Progress.Done(false, why, _changes.ConflictedCount);
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
        var local = (await _svn.GetLocalChangesAsync(wc, ct)).Count(s => s.Status is SvnStatusKind.Modified or SvnStatusKind.Added or SvnStatusKind.Deleted
            or SvnStatusKind.Replaced or SvnStatusKind.Missing or SvnStatusKind.Conflicted or SvnStatusKind.Obstructed or SvnStatusKind.Incomplete);
        lock (_lock)
        {
            _blocked = local == 0 ? string.Empty
                : Loc.T("La working copy ha {0} elementi con modifiche locali. Per unire serve una working copy pulita (così \"Annulla merge\" non tocca altro lavoro): fai prima Commit oppure Revert, poi riprova.", local);
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
