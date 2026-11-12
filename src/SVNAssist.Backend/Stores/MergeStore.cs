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
                return _error.Length > 0 ? (_error, true) : (_message, false);
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
                : new MergeDto(_source.Name, Title(_source), _loading, _busy, _error, _blocked, _selected.Order().ToList(), _entries.Count, _result, pending, SvnErrors.SuggestsUpdate(_error));
    }

    internal void Close()
    {
        lock (_lock)
        {
            _active = false;
            _entries = [];
            _selected.Clear();
            _result = null;
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
            _selected.Clear();
            _result = null;
            _error = string.Empty;
            _blocked = string.Empty;
            _message = Loc.T("Lettura delle revisioni da unire...");
        }

        Changed?.Invoke();
    }

    internal async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            if (await _svn.GetSvnVersionAsync(ct) is { } v && v < new Version(1, 8))
                throw new InvalidOperationException(Loc.T("SVN Merge richiede svn 1.8 o successivo (trovato {0}).", v));
            await ReloadEligibleAsync(ct);
            await CheckBlockedAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
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

    /// <summary>Rilegge le revisioni da unire e la working copy (dopo "Annulla merge"): l'esito precedente non vale piu'.</summary>
    internal async Task RefreshAsync(CancellationToken ct)
    {
        if (!Active)
            return;
        try
        {
            await ReloadEligibleAsync(ct);
            await CheckBlockedAsync(ct);
            lock (_lock)
                _result = null;
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
        }

        Changed?.Invoke();
    }

    internal void ClearSelection()
    {
        lock (_lock)
        {
            _selected.Clear();
            _result = null;
        }

        Changed?.Invoke();
    }

    /// <summary>Anteprima (<c>--dry-run</c>): le revisioni scelte, o tutte quelle da unire se non ce n'e' nessuna.</summary>
    internal async Task PreviewAsync(CancellationToken ct)
    {
        if (!TryBusy(out var source, out var wc, out var revs))
            return;
        try
        {
            await CheckBlockedAsync(ct); // l'anteprima non modifica nulla: serve solo ad aggiornare l'avviso
            var output = await _svn.MergeAsync(wc, source.Url, revs, dryRun: true, ct);
            lock (_lock)
                _result = ToResult("preview", revs is null ? AllLabel() : RevsLabel(revs), output);
            Say(Loc.T("Anteprima completata."));
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
            // Il merge richiede una working copy pulita: lo si ricontrolla sempre, e' cambiata dall'ultima volta.
            if (await CheckBlockedAsync(ct))
                return;

            var label = revs is null ? AllLabel() : RevsLabel(revs);
            MergeOutput? output = null;
            await _changes.RunExclusiveAsync(async () =>
            {
                try
                {
                    Say(Loc.T("Merge da {0} in corso...", source.Name));
                    output = await _svn.MergeAsync(wc, source.Url, revs, dryRun: false, ct);
                    var summary = ToResult("merge", label, output);
                    lock (_lock)
                        _result = summary;
                    IReadOnlyList<LogEntryDto> shown;
                    lock (_lock)
                        shown = _entries;
                    await _changes.RefreshAsync(ct, reloadBranch: false, output.Conflicts > 0
                        ? Loc.T("Merge completato: {0} file in conflitto da risolvere.", output.Conflicts)
                        : Loc.T("Merge completato: rivedi le modifiche e committa."));
                    // Dopo il refresh: il repository (chiave dei Recenti) e' noto.
                    _changes.AfterMerge(MergeCommitMessage(revs is null ? shown.Select(e => e.Rev).ToList() : revs, shown, source.Path),
                        Loc.T("Merge {0} da {1}: rivedi le modifiche e committa, oppure annulla il merge. Messaggio del merge disponibile nei Recenti.", label, "^/" + source.Path));
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Fail(ex);
                    await _changes.RefreshAsync(ct, reloadBranch: false); // un merge fallito a meta' puo' aver toccato dei file
                }
            });

            if (output is not null)
            {
                await ReloadEligibleAsync(ct);
                lock (_lock)
                    _selected.Clear();
                Say(output.Conflicts > 0
                    ? Loc.T("Merge eseguito, da committare. Conflitti da risolvere in Changes: {0}.", output.Conflicts)
                    : Loc.T("Merge eseguito, da committare."));
                await _changes.RevealConflictsAsync(output.Conflicts, ct);
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
    internal static string RevRanges(IEnumerable<long> revs)
    {
        var parts = new List<string>();
        long? start = null, prev = null;
        void Flush()
        {
            if (start is { } s)
                parts.Add(s == prev ? $"{s}" : $"{s}-{prev}");
        }

        foreach (var r in revs.Distinct().Order())
        {
            if (prev is { } p && r == p + 1)
            {
                prev = r;
                continue;
            }

            Flush();
            start = prev = r;
        }

        Flush();
        return string.Join(",", parts);
    }

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

    /// <summary>Elenca le revisioni del sorgente ancora da unire con autore, data e messaggio (<c>svn log</c> sul sorgente, filtrato lato client).</summary>
    private async Task ReloadEligibleAsync(CancellationToken ct)
    {
        string wc;
        SwitchPickerStore.Target source;
        lock (_lock)
        {
            wc = _wc;
            source = _source!;
        }

        var eligible = await _svn.GetEligibleRevisionsAsync(wc, source.Url, ct);
        var byRev = new Dictionary<long, LogEntryDto>();
        if (eligible.Count > 0)
        {
            foreach (var e in await _svn.GetLogRangeAsync(source.Url, eligible[0], eligible[^1], ct))
                byRev[e.Revision] = LogStore.ToDto(e);
        }

        // Una revisione che il log non restituisce (permessi) resta comunque unibile: riga senza dettagli.
        var entries = eligible.OrderByDescending(r => r)
            .Select(r => byRev.TryGetValue(r, out var d) ? d : new LogEntryDto(r, string.Empty, string.Empty, string.Empty, [])).ToList();
        lock (_lock)
        {
            _entries = entries;
            _selected.IntersectWith(entries.Select(e => e.Rev));
            _message = entries.Count == 0 ? Loc.T("Nessuna revisione da unire: {0} è già allineato.", source.Name) : Loc.T("{0} revisioni da unire.", entries.Count);
        }
    }

    /// <summary>Working copy con modifiche locali ai file versionati? Aggiorna <c>Blocked</c> e ritorna <c>true</c> se il merge e' da rifiutare.</summary>
    private async Task<bool> CheckBlockedAsync(CancellationToken ct)
    {
        string wc;
        lock (_lock)
            wc = _wc;
        var local = (await _svn.GetStatusAsync(wc, ct)).Count(s => s.Status is SvnStatusKind.Modified or SvnStatusKind.Added or SvnStatusKind.Deleted
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
