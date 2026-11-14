using System.IO;
using System.Text;
using System.Text.Json;
using SVNAssist.Models;
using SVNAssist.Rpc;
using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>
/// Vista "SVN Changes", sul modello di Git Changes: branch + Update, messaggio di commit con AI, sezioni
/// Staged / Modifiche / Non versionati (albero o elenco), checklist dei prerequisiti.
/// </summary>
/// <remarks>
/// Nessuna dipendenza da Visual Studio: lo stato e' un insieme di <see cref="ChangeEntry"/> (da <c>svn status</c>) piu' l'insieme
/// "Staged" (SVN non ha uno staging: e' una selezione per il commit tenuta in memoria per working copy). Lo snapshot
/// (<see cref="GetSnapshot"/>) e' calcolato al volo e conforme a changes-state.schema.json. I comandi arrivano da
/// <see cref="DispatchAsync"/> con le chiavi di riga della UI: ogni chiave viene <b>rivalidata</b> contro lo stato corrente
/// (<see cref="ResolveKey"/>) e i percorsi vengono sempre da <c>svn status</c>, mai dalla UI.
/// </remarks>
internal sealed class ChangesStore : ViewStore, IDisposable
{
    /// <summary>Oltre questo numero il diff di una cartella viene troncato (ogni file e' una chiamata a svn).</summary>
    internal const int MaxFolderDiffFiles = 100;
    private const int MaxListedInConfirmation = 8;

    /// <summary>Quiete richiesta dopo l'ultimo evento del file system prima di rileggere lo stato.</summary>
    internal static readonly TimeSpan RefreshQuietPeriod = TimeSpan.FromMilliseconds(800);

    private static readonly ChangeSection[] SectionOrder = [ChangeSection.Staged, ChangeSection.Changes, ChangeSection.Unversioned];

    private readonly ISvnService _svnService;
    private readonly ISvnAssistSettings _settingsService;
    private readonly IDialogService _dialogs;
    private readonly IHost _host;
    private readonly SemaphoreSlim _autoDetectLock = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RefreshDebouncer _debouncer;

    // Stato condiviso (righe, staged, espansione): lo toccano il refresh del watcher e i comandi dell'utente,
    // su thread diversi. Ogni mutazione/lettura composta passa da questo lock (rientrante).
    private readonly object _stateLock = new();

    // Un'operazione dell'utente alla volta (commit, update, revert...): il doppio click non la ripete e il refresh
    // automatico non gira mentre svn tiene il lock della working copy.
    private int _operationRunning;
    private readonly PendingSwitch _pendingSwitch;
    private int _started;

    private List<ChangeEntry> _entries = [];

    /// <summary>Lo stato dei file e' stato riletto da svn (per i glifi di Solution Explorer).</summary>
    internal event Action? EntriesChanged;

    /// <summary>Stato dei percorsi non normali (percorso completo -> nome di <see cref="SvnStatusKind"/>); conflitto ad albero = Conflicted.</summary>
    internal Dictionary<string, string> SccStates()
    {
        lock (_stateLock)
            return _entries.GroupBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Status.ToString(), StringComparer.OrdinalIgnoreCase);
    }
    private readonly Dictionary<string, WorkingCopyState> _wcStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    private string _workingCopyPath = string.Empty;
    private string? _branchLoadedFor;
    private bool _autoDetected;
    private bool _statusIsAuto = true;
    private string _statusMessage = Loc.T("Caricamento in corso...");
    private bool _showWorkingCopyInput;
    private bool _isTreeView = true;
    private bool _loading = true;
    private string _branchName = string.Empty;
    private string? _svnVersion;
    private string? _branchUrl, _repoRoot, _lastCommitWc;
    private long? _wcRev, _lastCommitRev;
    private int _statusSeq;
    private string _themeMode = "auto";
    private string _vsThemeKind = "dark";
    private string? _solutionRoot;

    private FileSystemWatcher? _watcher;
    private string _watchedRoot = string.Empty;
    private readonly object _watcherLock = new();

    /// <summary>Elementi in Staged e gia' visti per una working copy (i nuovi ricevono la selezione di default).</summary>
    private sealed class WorkingCopyState
    {
        public HashSet<string> Staged { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Known { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public ChangesStore(
        ISvnAssistSettings settingsService,
        ISvnService svnService,
        IAiServiceFactory aiServiceFactory,
        IAiSetupPrompt aiSetupPrompt,
        IDialogService dialogs,
        IHost host,
        PrereqStore prerequisites,
        WizardStore? wizard = null,
        string? recentTargetsFile = null)
    {
        _settingsService = settingsService;
        _svnService = svnService;
        _dialogs = dialogs;
        _host = host;
        Prerequisites = prerequisites;
        Prerequisites.SvnBecameAvailable = ct => RefreshAsync(ct, reloadBranch: true);
        Prerequisites.Changed += NotifyChanged;
        Wizard = wizard ?? new WizardStore(prerequisites, settingsService);
        Wizard.Changed += NotifyChanged;
        CommitMessage = new CommitStore(
            settingsService, svnService, aiServiceFactory, aiSetupPrompt,
            stagedPaths: () => CommitSet().Entries.Select(e => e.FullPath).ToList(),
            workingCopyPath: () => WorkingCopyPath,
            dialogs: dialogs);
        CommitMessage.Changed += NotifyChanged;
        RevPicker = new RevisionPickerStore(svnService);
        RevPicker.Changed += NotifyChanged;
        Switch = new SwitchPickerStore(svnService, settingsService, recentTargetsFile);
        Switch.Changed += NotifyChanged;
        Progress.Changed += NotifyChanged;
        // Accanto agli ultimi usati (nei test: un file temporaneo per test).
        _pendingSwitch = new PendingSwitch(recentTargetsFile is null ? null : recentTargetsFile + ".pending-switch.json");
        Switch.PendingUrl = wc => _pendingSwitch.Get(wc)?.Url;
        _debouncer = new RefreshDebouncer(RefreshQuietPeriod, AutoRefreshAsync);
        HeadCheck = new HeadWatcher(FetchHeadAsync, () => OperationRunning, settingsService.GetHeadCheckMinutesAsync, NotifyChanged);
    }

    /// <summary>Controllo periodico "altri hanno committato?" (indicatore della revisione nella barra di stato).</summary>
    internal HeadWatcher HeadCheck { get; }

    /// <summary>Ultimi messaggi di commit per repository (tendina "Recenti"); <c>null</c> = funzione spenta (test).</summary>
    internal RecentMessages? Recent { get; set; }

    private async Task<long?> FetchHeadAsync(CancellationToken ct)
    {
        string? url;
        lock (_stateLock)
            url = _branchUrl;
        return string.IsNullOrEmpty(url) ? null : await _svnService.GetHeadRevisionAsync(url, ct);
    }

    /// <summary>Refresh automatico (watcher): salta se non c'e' una working copy o se un'operazione dell'utente e' in corso.</summary>
    internal Task AutoRefreshAsync(CancellationToken ct)
        => string.IsNullOrWhiteSpace(WorkingCopyPath) || OperationRunning ? Task.CompletedTask : RefreshAsync(ct, reloadBranch: false);

    /// <summary>Messaggio di commit e azioni AI.</summary>
    internal CommitStore CommitMessage { get; }

    /// <summary>Scelta guidata della revisione per "Update to revision".</summary>
    internal RevisionPickerStore RevPicker { get; }

    /// <summary>Selettore di "SVN Switch..." (e della sorgente di "SVN Merge...").</summary>
    internal SwitchPickerStore Switch { get; }

    /// <summary>Finestra di avanzamento modale (switch, merge, update, cleanup), condivisa con la vista Log (il merge parte da li').</summary>
    internal OperationProgress Progress { get; } = new();

    // Switch su working copy sporca: destinazione in attesa della scelta dell'utente (avviso) e destinazione a cui si torna dopo il Cleanup.
    private SwitchPickerStore.Target? _warnTarget, _cleanupReturn;

    // "Commit All" con file non versionati: domanda prima di aggiungerli (Si' / No / Annulla).
    private bool _commitWarn;
    private bool _cleanupOpen;

    /// <summary>Esegue una chiamata svn convogliando le sue righe di notifica nella finestra di avanzamento.</summary>
    private Task<T> Streaming<T>(Func<Task<T>> call) => SvnProgress.RunAsync(Progress.Line, call);

    /// <summary>Chiude la finestra di avanzamento (a fine operazione); <paramref name="showConflicts"/> porta in primo piano Changes. Dopo un Cleanup lanciato dall'avviso dello switch torna l'avviso.</summary>
    internal async Task CloseProgressAsync(bool showConflicts)
    {
        Progress.Close();
        if (_cleanupReturn is { } back)
        {
            _cleanupReturn = null;
            _warnTarget = back;
            NotifyChanged();
        }

        if (showConflicts)
            await _host.ShowChangesWindowAsync();
    }

    /// <summary>Conteggi per tipo delle modifiche locali (i non versionati si contano a parte: non vengono portati dallo switch).</summary>
    internal static DirtyCountsDto CountDirty(IEnumerable<SvnStatusKind> statuses)
    {
        int m = 0, a = 0, d = 0, c = 0, u = 0;
        foreach (var s in statuses)
        {
            switch (s)
            {
                case SvnStatusKind.Modified or SvnStatusKind.Replaced or SvnStatusKind.Obstructed: m++; break;
                case SvnStatusKind.Added: a++; break;
                case SvnStatusKind.Deleted or SvnStatusKind.Missing: d++; break;
                case SvnStatusKind.Conflicted: c++; break;
                case SvnStatusKind.Unversioned: u++; break;
            }
        }

        return new DirtyCountsDto(m, a, d, c, u);
    }

    private static int Total(DirtyCountsDto c) => c.Modified + c.Added + c.Deleted + c.Conflicted + c.Unversioned;

    /// <summary>Dopo "Annulla merge": il Log in modalita' merge rilegge le revisioni da unire (tornano tutte da unire).</summary>
    internal Func<CancellationToken, Task>? MergeUndone { get; set; }

    /// <summary>Apre il Log in modalita' merge sulla sorgente scelta (la imposta il backend: il Log e' un'altra vista).</summary>
    internal Func<string, SwitchPickerStore.Target, CancellationToken, Task>? StartMerge { get; set; }

    // Merge eseguito e non ancora committato: banner con "Annulla merge".
    private string? _mergeBanner;

    /// <summary>
    /// Dopo un merge: banner con "Annulla merge" e messaggio di commit (stile TortoiseSVN) in cima ai Recenti del repository.
    /// Il campo del messaggio non si tocca: il messaggio resta nei Recenti anche dopo un revert e lo si inserisce da li' se serve.
    /// </summary>
    internal void AfterMerge(string commitText, string bannerText)
    {
        string? repo;
        lock (_stateLock)
        {
            _mergeBanner = bannerText;
            repo = _repoRoot;
        }

        if (!string.IsNullOrEmpty(repo))
            Recent?.Add(repo, commitText);
        NotifyChanged();
    }

    private void ClearMergeBanner()
    {
        lock (_stateLock)
        {
            _mergeBanner = null;
        }
    }

    /// <summary>Merge eseguito e non ancora committato ne' annullato (c'e' ancora qualcosa da committare): il Log in modalita' merge lo mostra al posto di "0 da unire".</summary>
    internal bool MergePending
    {
        get
        {
            lock (_stateLock)
                return _mergeBanner is not null && CommitSet().Entries.Count > 0;
        }
    }

    // Conflitti di testo/proprieta' con i dati di svn info (tipo e file di appoggio): chiave = percorso completo.
    private IReadOnlyDictionary<string, SvnConflictInfo> _conflictInfo = new Dictionary<string, SvnConflictInfo>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Avvia l'editor di merge a 3 vie (di default vsDiffMerge, poi TortoiseMerge); ritorna il suo nome o <c>null</c> se non c'e' (iniettabile nei test).</summary>
    internal Func<MergeEditorFiles, string?> MergeEditorLauncher { get; set; } = MergeEditor.TryLaunch;

    /// <summary>Porta in primo piano la finestra Changes (dal Log: "Risolvi i conflitti").</summary>
    internal Task ShowWindowAsync() => _host.ShowChangesWindowAsync();

    /// <summary>
    /// Dopo un merge/update/switch che lascia conflitti: porta in primo piano Changes (sezione Conflitti), senza chiedere nulla.
    /// </summary>
    internal async Task RevealConflictsAsync(int conflicts, CancellationToken ct)
    {
        if (conflicts <= 0)
            return;
        await _host.ShowChangesWindowAsync();
        SetStatus(Loc.T("{0} file in conflitto: risolvili nella sezione Conflitti, poi committa.", conflicts));
    }

    /// <summary>Checklist dei prerequisiti.</summary>
    internal PrereqStore Prerequisites { get; }

    /// <summary>Procedura guidata per configurare l'AI.</summary>
    internal WizardStore Wizard { get; }

    /// <summary>Se <c>true</c> un FileSystemWatcher sulla working copy rinfresca lo stato da solo (spento nei test).</summary>
    internal bool EnableWatcher { get; set; }

    /// <summary>Verifica se un percorso e' una cartella (iniettabile nei test: di default il file system).</summary>
    internal Func<string, bool> IsDirectory { get; set; } = Directory.Exists;

    /// <summary>Elimina un file non versionato dal disco: di default nel cestino (iniettabile nei test).</summary>
    internal Action<string> DeleteFromDisk { get; set; } = RecycleOrDelete;

    /// <summary>Cartella temporanea per i file del diff (BASE esportato, diff di cartella).</summary>
    internal string TempRoot { get; set; } = Path.Combine(Path.GetTempPath(), "SVNAssist", "diff");

    /// <summary>Percorso della working copy SVN da analizzare.</summary>
    internal string WorkingCopyPath
    {
        get { lock (_stateLock) return _workingCopyPath; }
        set
        {
            lock (_stateLock)
            {
                if (!string.Equals(_workingCopyPath, value, StringComparison.OrdinalIgnoreCase))
                    _mergeBanner = null; // il merge riguardava un'altra working copy
                _workingCopyPath = value;
            }
        }
    }

    private bool OperationRunning => Volatile.Read(ref _operationRunning) != 0;

    private WorkingCopyState Wc
    {
        get
        {
            lock (_stateLock)
            {
                if (!_wcStates.TryGetValue(_workingCopyPath, out var state))
                    _wcStates[_workingCopyPath] = state = new WorkingCopyState();
                return state;
            }
        }
    }

    // ── Snapshot ─────────────────────────────────────────────────────

    /// <inheritdoc />
    internal override object GetSnapshot()
    {
        lock (_stateLock)
        {
            var sections = SectionOrder.Select(section =>
            {
                var items = _entries.Where(e => SectionOf(e) == section).ToList();
                var id = ChangesTreeBuilder.SectionId(section);
                return new SectionDto(id, ChangesTreeBuilder.SectionTitle(section), items.Count,
                    !_collapsed.Contains(SectionCollapseKey(id)), BuildRows(section, items));
            }).ToList();

            var download = Prerequisites.Download;
            var mode = _themeMode;
            var commit = CommitSet();
            var conflicts = BuildConflicts();
            return new ChangesState(
                BuildWorkingCopy(),
                CommitMessage.GetSnapshot(download, commit.Entries.Count, commit.All) with { Recent = RecentFor(_repoRoot) },
                _isTreeView ? "tree" : "list",
                new ThemeDto(mode, mode == "auto" ? _vsThemeKind : mode),
                sections,
                BuildStatus(),
                Prerequisites.GetSnapshot(),
                _loading,
                Wizard.GetSnapshot(),
                RevPicker.GetSnapshot(),
                Switch.GetSnapshot(),
                // Il banner sparisce da solo se non resta nessuna modifica (annullata a mano, committata altrove).
                _mergeBanner is not null && commit.Entries.Count > 0 ? new MergeBannerDto(_mergeBanner, conflicts.Count) : null,
                conflicts,
                Progress.GetSnapshot(),
                _warnTarget is { } w ? BuildSwitchWarn(w) : null,
                _cleanupOpen ? new CleanupDialogDto(CleanupOptions.ExtrasSupported(SvnVersionParsed), _svnVersion ?? string.Empty, _cleanupReturn?.Name ?? string.Empty) : null,
                _commitWarn ? BuildCommitWarn() : null);
        }
    }

    private CommitWarnDto BuildCommitWarn()
    {
        var names = _entries.Where(e => e.Status == SvnStatusKind.Unversioned).Select(e => e.RelativePath).ToList();
        return new CommitWarnDto(names.Count, names.Take(10).ToList(), Math.Max(0, names.Count - 10));
    }

    /// <summary>"Commit All" (Staged vuoto) con un messaggio e dei non versionati: prima si chiede se aggiungerli.</summary>
    private bool NeedsNewFilesQuestion()
    {
        if (string.IsNullOrWhiteSpace(CommitMessage.Text))
            return false;
        lock (_stateLock)
            return CommitSet().All && _entries.Any(e => e.Status == SvnStatusKind.Unversioned);
    }

    private Version? SvnVersionParsed => Version.TryParse(_svnVersion, out var v) ? v : null;

    private SwitchWarnDto BuildSwitchWarn(SwitchPickerStore.Target target)
    {
        var counts = CountDirty(_entries.Select(e => e.Status));
        return new SwitchWarnDto(target.Name, counts, Total(counts));
    }

    private IReadOnlyList<string> RecentFor(string? repo) => Recent is null || string.IsNullOrEmpty(repo) ? [] : Recent.Get(repo);

    /// <summary>Working copy con l'indicatore della revisione: verde alla HEAD del ramo, arancio se il server ha di piu', neutro se non si sa (con il perche').</summary>
    private WorkingCopyDto BuildWorkingCopy()
    {
        var head = HeadCheck.Head;
        var state = RevisionIndicator.Compute(_wcRev, head);
        string? note = null;
        if (state == "unknown" && _wcRev is > 0)
            note = HeadCheck.Error is { } err ? Loc.T("Impossibile controllare le nuove revisioni: {0}", FirstLine(err))
                : HeadCheck.Disabled ? Loc.T("Controllo delle nuove revisioni spento (Tools > Options > SVNAssist > Controllo nuove revisioni).")
                : Loc.T("Controllo delle nuove revisioni in corso...");
        // Switch interrotto: la radice e' gia' sul ramo nuovo ma il resto no, il nome da solo ingannerebbe.
        var branch = _branchName.Length > 0 && _pendingSwitch.Get(_workingCopyPath) is not null ? Loc.T("{0} (switch incompleto)", _branchName) : _branchName;
        return new WorkingCopyDto(_workingCopyPath, _showWorkingCopyInput, branch, _wcRev, _svnVersion, head, _wcRev is > 0 ? state : null, note);
    }

    /// <summary>Elementi in conflitto con tipo e file di appoggio (da <c>svn info</c>, mai indovinati dal nome).</summary>
    private List<ConflictDto> BuildConflicts()
    {
        var wc = _workingCopyPath;
        return _entries.Where(e => e.Status == SvnStatusKind.Conflicted).Select(e =>
        {
            _conflictInfo.TryGetValue(e.FullPath, out var info);
            var kind = e.IsTreeConflict ? "tree" : info?.Type == "property" ? "property" : "text";
            return new ConflictDto("changes:" + e.RelativePath, e.RelativePath, e.Name, kind,
                (info?.Files ?? []).Select(f => Path.GetRelativePath(wc, f).Replace('\\', '/')).ToList());
        }).ToList();
    }

    private static string SectionCollapseKey(string sectionId) => "sec:" + sectionId;

    private ChangeSection SectionOf(ChangeEntry entry)
    {
        // I conflitti non si possono mettere in Staged (svn non li committa): restano in Modifiche.
        if (entry.Status != SvnStatusKind.Conflicted && Wc.Staged.Contains(entry.FullPath))
            return ChangeSection.Staged;

        return entry.Status == SvnStatusKind.Unversioned ? ChangeSection.Unversioned : ChangeSection.Changes;
    }

    private List<RowDto> BuildRows(ChangeSection section, List<ChangeEntry> items)
    {
        var rows = new List<RowDto>();
        var nodes = _isTreeView ? ChangesTreeBuilder.BuildTree(section, items) : ChangesTreeBuilder.BuildList(section, items);
        AddNodes(rows, nodes, section, depth: 0);
        return rows;
    }

    private void AddNodes(List<RowDto> rows, IReadOnlyList<ChangeNode> nodes, ChangeSection section, int depth)
    {
        foreach (var node in nodes)
        {
            if (node.IsFolder)
            {
                var expanded = !_collapsed.Contains(node.Key);
                rows.Add(new RowDto(node.Key, "folder", depth, node.Label, Detail: string.Empty, Dir: null, Expanded: expanded,
                    Count: node.Count, Status: null, StatusText: string.Empty, Can: FolderCan(section, node.Entries().ToList()),
                    Tooltip: node.FolderPath));
                if (expanded)
                    AddNodes(rows, node.Children, section, depth + 1);
                continue;
            }

            var entry = node.Entry!;
            var statusName = StatusPresentation.DisplayName(entry.Status, entry.IsTreeConflict);
            var statusText = statusName;
            if (entry.IsLocked && entry.Status != SvnStatusKind.Normal)
                statusText += " — " + Loc.T("bloccato (lock)");
            if (section == ChangeSection.Staged && entry.Status == SvnStatusKind.Unversioned)
                statusText += " " + Loc.T("(verrà aggiunto con il commit)");
            else if (section == ChangeSection.Staged && entry.Status == SvnStatusKind.Missing)
                statusText += " " + Loc.T("(verrà eliminato con il commit)");

            var isRoot = entry.RelativePath == ".";
            rows.Add(new RowDto(node.Key, "file", depth,
                isRoot ? Loc.T("(cartella radice)") : node.Label,
                Detail: _isTreeView ? string.Empty : entry.FolderPath,
                Dir: entry.IsDirectory, Expanded: null, Count: null,
                Status: StatusPresentation.ShortCode(entry.Status), StatusText: statusText,
                Can: new CanDto(
                    Diff: CanDiff(entry) || (entry.Status == SvnStatusKind.Unversioned && !entry.IsDirectory),
                    Stage: section != ChangeSection.Staged && entry.Status is not (SvnStatusKind.Conflicted or SvnStatusKind.Normal),
                    Unstage: section == ChangeSection.Staged,
                    Add: entry.Status == SvnStatusKind.Unversioned,
                    Ignore: entry.Status == SvnStatusKind.Unversioned,
                    Revert: StatusPresentation.IsRevertable(entry.Status),
                    Resolve: entry.Status == SvnStatusKind.Conflicted,
                    DeleteFile: CanDeleteFile(entry),
                    Lock: !entry.IsDirectory && !entry.IsLocked && entry.Status == SvnStatusKind.Modified,
                    Unlock: !entry.IsDirectory && entry.IsLocked),
                Tooltip: $"{entry.RelativePath} — {statusText}",
                Locked: entry.IsLocked ? true : null));
        }
    }

    private static bool CanDiff(ChangeEntry e) => e.Status is not (SvnStatusKind.Unversioned or SvnStatusKind.Normal) && !e.IsDirectory;

    private static bool CanDeleteFile(ChangeEntry e) => e.Status != SvnStatusKind.Deleted && e.RelativePath != "."; // mai la radice della working copy

    private static CanDto FolderCan(ChangeSection section, List<ChangeEntry> entries) => new(
        Diff: entries.Any(CanDiff),
        Stage: section != ChangeSection.Staged && entries.Any(e => e.Status is not (SvnStatusKind.Conflicted or SvnStatusKind.Normal)),
        Unstage: section == ChangeSection.Staged,
        Add: entries.Any(e => e.Status == SvnStatusKind.Unversioned),
        Ignore: entries.Any(e => e.Status == SvnStatusKind.Unversioned),
        Revert: entries.Any(e => StatusPresentation.IsRevertable(e.Status)),
        Resolve: false,
        DeleteFile: entries.Any(CanDeleteFile));

    // ── Comandi ──────────────────────────────────────────────────────

    /// <inheritdoc />
    internal override async Task<object?> DispatchAsync(string name, JsonElement args, CancellationToken ct)
    {
        switch (name)
        {
            case "refresh":
                // Refresh esplicito: rilegge anche il branch e torna a mostrare il riepilogo.
                _statusIsAuto = true;
                if (!OperationRunning)
                    await RefreshAsync(ct, reloadBranch: true, showLoading: true);
                break;
            case "update":
                await RunExclusiveAsync(() => UpdateCoreAsync(ct));
                break;
            case "updateToRevision":
                await OpenRevisionPickerAsync([WorkingCopyPath], ct);
                break;
            case "revPickerMore":
                await RevPicker.MoreAsync(ct);
                break;
            case "revPickerCancel":
                RevPicker.Close();
                break;
            case "revPickerOk":
                await RevPickerOkAsync(Args.Str(args, "rev"), ct);
                break;
            case "switchOpen":
                if (string.IsNullOrWhiteSpace(WorkingCopyPath))
                    SetStatus(Loc.T("Nessuna working copy configurata."));
                else
                    await Switch.OpenAsync(WorkingCopyPath, ct);
                break;
            case "mergeOpen":
                if (string.IsNullOrWhiteSpace(WorkingCopyPath))
                    SetStatus(Loc.T("Nessuna working copy configurata."));
                else
                    await Switch.OpenAsync(WorkingCopyPath, ct, mode: "merge");
                break;
            case "mergeUndo":
                await RunExclusiveAsync(() => UndoMergeCoreAsync(ct));
                break;
            case "mergeDismiss":
                ClearMergeBanner();
                NotifyChanged();
                break;
            case "switchRefresh":
                await Switch.RefreshAsync(ct);
                break;
            case "switchCancel":
                Switch.Close();
                break;
            case "switchOk":
                await SwitchOkAsync(Args.Str(args, "name"), ct);
                break;
            case "switchResume":
                await RunExclusiveAsync(() => ResumeSwitchCoreAsync(ct));
                break;
            case "switchBack":
                await RunExclusiveAsync(() => SwitchBackCoreAsync(ct));
                break;
            case "conflictOp":
                await RunExclusiveAsync(() => ConflictOpAsync(Args.OneOf(args, "op", "edit", "mine", "theirs", "resolved"), Args.Str(args, "key"), ct));
                break;
            case "resolveTreeConflicts":
                await RunExclusiveAsync(() => ResolveTreeConflictsAsync(ct));
                break;
            case "showLog":
                if (string.IsNullOrWhiteSpace(WorkingCopyPath))
                    SetStatus(Loc.T("Nessuna working copy configurata."));
                else
                    await _host.ShowLogAsync(WorkingCopyPath);
                break;
            case "cleanup":
                // Un solo punto d'ingresso per il Cleanup (menu di VS, errore "working copy bloccata"): la finestra delle opzioni.
                if (string.IsNullOrWhiteSpace(WorkingCopyPath))
                    SetStatus(Loc.T("Nessuna working copy configurata."));
                else
                {
                    if (Progress.GetSnapshot() is { Running: false })
                        Progress.Close(); // "Cleanup…" dalla finestra di un'operazione annullata: la finestra delle opzioni la sostituisce
                    OpenCleanupDialog();
                }

                break;
            case "cleanupCancel":
                CloseCleanupDialog(back: true);
                break;
            case "cleanupOk":
                await CleanupOkAsync(args, ct);
                break;
            case "switchWarnContinue":
                if (_warnTarget is { } go)
                {
                    _warnTarget = null;
                    await StartSwitchAsync(go, ct);
                }

                break;
            case "switchWarnCleanup":
                if (_warnTarget is { } back)
                {
                    _warnTarget = null;
                    _cleanupReturn = back;
                    OpenCleanupDialog();
                }

                break;
            case "switchWarnCancel":
                _warnTarget = null;
                NotifyChanged();
                break;
            case "progressClose":
                await CloseProgressAsync(Args.Bool(args, "conflicts"));
                break;
            case "opCancel":
                Progress.Cancel();
                break;
            case "progressRevertMerge":
                await RevertCancelledMergeAsync(ct);
                break;
            case "commit":
                if (NeedsNewFilesQuestion())
                {
                    _commitWarn = true;
                    NotifyChanged();
                    break;
                }

                await RunExclusiveAsync(() => CommitCoreAsync(ct));
                break;
            case "commitWarnAdd":
            case "commitWarnNo":
                _commitWarn = false;
                NotifyChanged();
                await RunExclusiveAsync(() => CommitCoreAsync(ct, addNew: name == "commitWarnAdd"));
                break;
            case "commitWarnCancel":
                _commitWarn = false;
                NotifyChanged();
                break;
            case "setCommitText":
                CommitMessage.SetTextQuietly(Args.Str(args, "text"));
                break;
            case "useRecentMessage":
                {
                    // L'indice si rivalida contro l'elenco corrente (potrebbe essere cambiato dopo un commit).
                    string? repo;
                    lock (_stateLock)
                        repo = _repoRoot;
                    var recent = RecentFor(repo);
                    var i = Args.Long(args, "i");
                    if (i >= 0 && i < recent.Count)
                        CommitMessage.Text = recent[(int)i];
                    break;
                }
            case "genAi":
                await CommitMessage.GenerateAsync(ct);
                break;
            case "improveAi":
                await CommitMessage.ImproveAsync(ct, args.ValueKind == JsonValueKind.Object && args.TryGetProperty("style", out _) ? Args.OneOf(args, "style", "shorter", "detailed") : null);
                break;
            case "setLanguage":
                CommitMessage.SetLanguage(Args.OneOf(args, "lang", "auto", "italiano", "english"));
                break;
            case "setWorkingCopy":
                await SetWorkingCopyAsync(Args.Str(args, "path"), ct);
                break;
            case "stage":
                SetStaged(Args.Str(args, "key"), staged: true);
                break;
            case "unstage":
                SetStaged(Args.Str(args, "key"), staged: false);
                break;
            case "toggleFolder":
                ToggleFolder(Args.Str(args, "key"));
                break;
            case "toggleSection":
                ToggleKey(SectionCollapseKey(Args.OneOf(args, "sec", "staged", "changes", "unversioned")));
                break;
            case "expandAll":
                SetAllExpanded(true);
                break;
            case "collapseAll":
                SetAllExpanded(false);
                break;
            case "setView":
                lock (_stateLock)
                    _isTreeView = Args.OneOf(args, "view", "tree", "list") == "tree";
                NotifyChanged();
                break;
            case "showDiff":
                await ShowDiffAsync(Args.Str(args, "key"), ct);
                break;
            case "showFolderDiff":
                await ShowFolderDiffAsync(Args.Str(args, "key"), ct);
                break;
            case "rowOp":
                await RowOpAsync(Args.OneOf(args, "op", "add", "ignore", "revert", "resolve", "deleteFile", "lock", "unlock"), Args.Str(args, "key"), ct);
                break;
            case "setTheme":
                await SetThemeAsync(Args.OneOf(args, "mode", "auto", "light", "dark", "blue"), ct);
                break;
            case "prereqToggle":
                Prerequisites.Toggle();
                break;
            case "prereqRecheck":
                await Prerequisites.RefreshAsync(ct);
                break;
            case "prereqRun":
                await Prerequisites.RunActionAsync(Args.Str(args, "id"), Args.Bool(args, "primary"), ct);
                break;
            case "wizardOpen":
                await Wizard.OpenAsync(false, ct);
                break;
            case "wizardOpenPrereq":
                await Wizard.OpenPrereqAsync(ct);
                break;
            case "openOptions":
                Wizard.Cancel();
                await _host.OpenSettingsAsync();
                break;
            case "wizardCancel":
                Wizard.Cancel();
                break;
            case "wizardPick":
                Wizard.Pick(Args.Str(args, "provider"));
                break;
            case "wizardBack":
                Wizard.Back();
                break;
            case "wizardNext":
                await Wizard.NextAsync(ct);
                break;
            case "wizardRecheck":
                await Wizard.RefreshAsync(ct);
                break;
            case "wizardRun":
                await Wizard.RunAsync(Args.Str(args, "id"), Args.Bool(args, "primary"), ct);
                break;
            case "wizardLogin":
                await Wizard.LoginAsync(ct);
                break;
            case "wizardSetAuth":
                await Wizard.SetAuthAsync(Args.Str(args, "endpoint"), Args.Str(args, "key"), ct);
                break;
            case "wizardFinish":
                if (await Wizard.FinishAsync(Args.Str(args, "model"), ct))
                    await CommitMessage.GenerateAsync(ct);
                break;
            default:
                throw new ArgumentException(Loc.T("Comando sconosciuto: {0}", name));
        }

        return null;
    }

    /// <summary>Tema scelto dall'utente: lo salva l'host (impostazione "windowTheme") e lo snapshot lo riporta.</summary>
    private async Task SetThemeAsync(string mode, CancellationToken ct)
    {
        await _settingsService.SetWindowThemeAsync(mode, ct);
        lock (_stateLock)
            _themeMode = mode;
        NotifyChanged();
    }

    /// <summary>L'host comunica il tema di Visual Studio (light/dark/blue): serve per <c>theme.kind</c> quando il modo e' "auto".</summary>
    internal void SetVsThemeKind(string kind)
    {
        if (kind is not ("light" or "dark" or "blue"))
            return;

        lock (_stateLock)
            _vsThemeKind = kind;
        NotifyChanged();
    }

    // ── Avvio e auto-detect ──────────────────────────────────────────

    /// <inheritdoc />
    internal override Task OnReadyAsync(CancellationToken ct)
    {
        // Primo ready: l'UI mostra subito "Caricamento in corso..."; sonda dei prerequisiti e primo svn status
        // (lenti su working copy grandi o rete lenta) girano in background e aggiornano lo snapshot.
        if (Interlocked.Exchange(ref _started, 1) == 0)
            _ = Task.Run(() => StartAsync(_lifetime.Token));
        return Task.CompletedTask;
    }

    private async Task StartAsync(CancellationToken ct)
    {
        try
        {
            _ = _debouncer.RunAsync(ct);
            await InitializeAsync(ct);
            // Il controllo delle nuove revisioni parte con la finestra (prima apertura) e non blocca il resto.
            if (EnableWatcher)
                _ = Task.Run(() => HeadCheck.RunAsync(ct), ct);
            await ApplyAutoDetectedWorkingCopyAsync(SolutionRootOrNull(), ct);
        }
        catch (OperationCanceledException)
        {
            // chiusura
        }
        catch (Exception ex)
        {
            BackendLog.Error("Avvio vista Changes", ex);
            ReportInitializationError(ex.Message);
        }
    }

    private string? SolutionRootOrNull()
    {
        lock (_stateLock)
            return _solutionRoot;
    }

    /// <summary>Controlla i prerequisiti (apre la checklist se manca svn), carica lingua e tema.</summary>
    internal async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            var theme = (await _settingsService.GetWindowThemeAsync(ct)).Trim().ToLowerInvariant();
            lock (_stateLock)
                _themeMode = theme is "light" or "dark" or "blue" ? theme : "auto";

            await Prerequisites.InitializeAsync(ct);
            await CommitMessage.InitializeAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Prerequisites.Feedback = Loc.T("Controllo prerequisiti non riuscito: {0}", ex.Message);
        }

        NotifyChanged();
    }

    /// <summary>Impostazioni cambiate in Tools > Options: ricontrolla tema e prerequisiti e riapplica la working copy (il setting vince sull'auto-detect).</summary>
    internal async Task OnSettingsChangedAsync(CancellationToken ct)
    {
        var theme = (await _settingsService.GetWindowThemeAsync(ct)).Trim().ToLowerInvariant();
        lock (_stateLock)
            _themeMode = theme is "light" or "dark" or "blue" ? theme : "auto";

        HeadCheck.Nudge(); // intervallo cambiato (o acceso/spento): rilegge subito l'impostazione
        if (Volatile.Read(ref _started) == 1)
        {
            // Rilegge anche i testi che dipendono dalla lingua dell'interfaccia (checklist, procedura guidata, riepilogo).
            await Prerequisites.RefreshAsync(ct);
            if (Wizard.GetSnapshot().Open)
                await Wizard.RefreshAsync(ct);
            await ApplyAutoDetectedWorkingCopyAsync(SolutionRootOrNull(), ct);
            if (_statusIsAuto)
                SetStatusAuto(BuildSummary());
        }

        NotifyChanged();
    }

    /// <summary>La solution di Visual Studio e' cambiata (<c>null</c> = nessuna): rileva la root SVN e carica lo stato.</summary>
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
                root = null; // percorso non valido
            }
        }

        lock (_stateLock)
            _solutionRoot = root;

        // Prima del primo ready non si carica nulla: ci pensa StartAsync (con il valore appena salvato).
        if (Volatile.Read(ref _started) == 1)
            await ApplyAutoDetectedWorkingCopyAsync(root, ct);
    }

    /// <summary>Applica il risultato dell'auto-detect della working copy dalla solution aperta.</summary>
    internal async Task ApplyAutoDetectedWorkingCopyAsync(string? svnRoot, CancellationToken ct)
    {
        // Regola: il setting "workingCopyPath", se valorizzato, vince sempre sull'auto-detect.
        var configured = (await _settingsService.GetWorkingCopyPathAsync(ct)).Trim();
        if (configured.Length > 0)
            svnRoot = configured;

        await _autoDetectLock.WaitAsync(ct);
        try
        {
            if (string.IsNullOrWhiteSpace(svnRoot))
            {
                // Un percorso scritto a mano non va azzerato dal cambio di solution.
                if (_autoDetected)
                {
                    _autoDetected = false;
                    WorkingCopyPath = string.Empty;
                    lock (_stateLock)
                    {
                        _branchName = string.Empty;
                        _branchLoadedFor = null;
                        _entries = [];
                        _showWorkingCopyInput = true;
                    }
                }

                if (string.IsNullOrWhiteSpace(WorkingCopyPath))
                {
                    lock (_stateLock)
                    {
                        _showWorkingCopyInput = true;
                        _loading = false;
                    }

                    SetStatusAuto(Loc.T("La solution corrente non è una working copy SVN."));
                }

                return;
            }

            // Stessa working copy: niente refresh (il watcher si occupa dei cambiamenti).
            if (string.Equals(WorkingCopyPath, svnRoot, StringComparison.OrdinalIgnoreCase))
                return;

            _autoDetected = true;
            WorkingCopyPath = svnRoot;
            ConfigureWatcher(svnRoot);
            lock (_stateLock)
                _showWorkingCopyInput = false;
            SetStatusAuto(Loc.T("Working copy SVN rilevata automaticamente, aggiornamento..."));
            await RefreshAsync(ct, reloadBranch: true, showLoading: true);
        }
        finally
        {
            _autoDetectLock.Release();
        }
    }

    /// <summary>Errore imprevisto durante il caricamento iniziale in background: lo mostra invece di lasciare "Caricamento...".</summary>
    internal void ReportInitializationError(string message)
    {
        lock (_stateLock)
            _loading = false;
        SetStatus(Loc.T("Errore durante il caricamento: {0}", message));
    }

    /// <summary>
    /// Working copy digitata dall'utente (unico percorso che arriva dalla UI): deve essere una cartella esistente
    /// dentro una working copy SVN.
    /// </summary>
    private async Task SetWorkingCopyAsync(string typed, CancellationToken ct)
    {
        var path = typed.Trim();
        if (path.Length == 0)
        {
            SetStatus(Loc.T("Inserisci il percorso della working copy."));
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            SetStatus(Loc.T("Errore: percorso non valido."));
            return;
        }

        if (!Directory.Exists(full))
        {
            SetStatus(Loc.T("Errore: cartella non trovata: {0}", full));
            return;
        }

        if (SvnService.FindSvnRoot(full) is null)
        {
            SetStatus(Loc.T("Errore: non è una working copy SVN: {0}", full));
            return;
        }

        _autoDetected = false;
        lock (_stateLock)
        {
            _entries = [];
            _branchLoadedFor = null;
            _workingCopyPath = full;
            _showWorkingCopyInput = true;
        }

        ConfigureWatcher(full);
        _statusIsAuto = true;
        await RefreshAsync(ct, reloadBranch: true, showLoading: true);
    }

    // ── Refresh ──────────────────────────────────────────────────────

    /// <summary>
    /// Legge <c>svn status</c> e ricostruisce lo stato. <c>svn info</c> (branch) solo se la working copy
    /// e' cambiata o se <paramref name="reloadBranch"/> (update, commit, refresh esplicito).
    /// </summary>
    internal async Task RefreshAsync(CancellationToken ct, bool reloadBranch = false, string? statusMessage = null, bool showLoading = false)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            if (showLoading)
            {
                lock (_stateLock)
                    _loading = true;
                NotifyChanged();
            }

            if (string.IsNullOrWhiteSpace(WorkingCopyPath))
            {
                var settingsPath = (await _settingsService.GetWorkingCopyPathAsync(ct)).Trim();
                if (settingsPath.Length > 0)
                    WorkingCopyPath = settingsPath;
            }

            var path = WorkingCopyPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                lock (_stateLock)
                    _showWorkingCopyInput = true;
                SetStatusAuto(Loc.T("Inserisci il percorso della working copy."));
                return;
            }

            var needBranch = reloadBranch || !string.Equals(_branchLoadedFor, path, StringComparison.OrdinalIgnoreCase);

            var statusTask = _svnService.GetStatusAsync(path, ct);
            var branchTask = needBranch ? LoadBranchAsync(path, ct) : null;

            var statuses = await statusTask;
            BranchInfo? branch = null;
            if (branchTask is not null)
                branch = await branchTask;
            var urlChanged = false;

            var newEntries = statuses
                .Where(s => s.Status != SvnStatusKind.Normal || s.IsLocked)
                .Select(s => ToEntry(s, path))
                .Where(e => e is not null)
                .Select(e => e!)
                .ToList();

            // I file di appoggio di un conflitto (.mine, .rNN, .merge-left/right, .working, .prej) sono non versionati: si leggono da svn info
            // (mai indovinati dal nome) e si tolgono dall'elenco, restano sotto il file in conflitto.
            var conflictedPaths = newEntries.Where(e => e.Status == SvnStatusKind.Conflicted && !e.IsTreeConflict).Select(e => e.FullPath).ToList();
            var conflictInfo = conflictedPaths.Count > 0
                ? await _svnService.GetConflictInfoAsync(conflictedPaths, ct)
                : new Dictionary<string, SvnConflictInfo>();
            var auxFiles = conflictInfo.Values.SelectMany(i => i.Files).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (auxFiles.Count > 0)
                newEntries.RemoveAll(e => e.Status == SvnStatusKind.Unversioned && auxFiles.Contains(e.FullPath));

            lock (_stateLock)
            {
                if (branch is { } b)
                {
                    _branchName = b.Label;
                    _svnVersion = b.Version;
                    _branchLoadedFor = path;
                    // La revisione di un nostro commit conta come minimo: il commit non porta la radice della working copy alla nuova revisione.
                    var floor = string.Equals(_lastCommitWc, path, StringComparison.OrdinalIgnoreCase) ? _lastCommitRev : null;
                    _wcRev = b.Rev is null ? null : Math.Max(b.Rev.Value, floor ?? 0);
                    _repoRoot = b.Root;
                    urlChanged = !string.Equals(_branchUrl, b.Url, StringComparison.Ordinal);
                    _branchUrl = b.Url;
                }

                // La working copy puo' essere cambiata mentre svn girava: il risultato vecchio si scarta.
                if (!string.Equals(_workingCopyPath, path, StringComparison.OrdinalIgnoreCase))
                    return;

                _entries = newEntries;
                _conflictInfo = conflictInfo;
                // Una working copy digitata a mano resta modificabile; una rilevata dalla solution nasconde il campo.
                _showWorkingCopyInput = !_autoDetected;
                UpdateStagedAfterRefresh();
            }

            EntriesChanged?.Invoke();

            if (urlChanged)
            {
                // Altro ramo (switch) o altra working copy: l'esito vecchio non vale piu', se ne chiede uno nuovo.
                HeadCheck.Reset();
                HeadCheck.Nudge();
            }

            if (statusMessage is not null)
                SetStatus(statusMessage);
            else if (_statusIsAuto)
                SetStatusAuto(BuildSummary());
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore: {0}", ex.Message));
            // Una working copy rilevata dalla solution non si "perde" per un errore passeggero (es. lock di svn).
            if (!_autoDetected)
            {
                lock (_stateLock)
                    _showWorkingCopyInput = true;
            }
        }
        finally
        {
            lock (_stateLock)
                _loading = false;
            _refreshGate.Release();
            NotifyChanged();
        }
    }

    private ChangeEntry? ToEntry(SvnFileStatus status, string workingCopy)
    {
        var fullPath = NormalizeStatusPath(status.Path, workingCopy);
        string relative;
        if (string.Equals(fullPath.TrimEnd('\\', '/'), workingCopy.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            relative = ".";
        }
        else
        {
            relative = Path.GetRelativePath(workingCopy, fullPath).Replace('\\', '/');
            // Mai elementi fuori dalla working copy (es. percorsi anomali da svn status).
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                BackendLog.Warn($"Elemento fuori dalla working copy ignorato: {status.Path}");
                return null;
            }
        }

        return new ChangeEntry(fullPath, relative, status.Status, status.IsTreeConflict, IsDirectory(fullPath), status.IsLocked);
    }

    /// <summary>Come Git: nulla entra in Staged da solo (restano in Modifiche); si tiene la scelta dell'utente e si tolgono i percorsi spariti.</summary>
    private void UpdateStagedAfterRefresh()
    {
        var state = Wc;
        var current = new HashSet<string>(_entries.Select(e => e.FullPath), StringComparer.OrdinalIgnoreCase);
        state.Staged.RemoveWhere(p => !current.Contains(p));
        state.Known = current;
    }

    private string BuildSummary()
    {
        List<ChangeEntry> entries;
        lock (_stateLock)
            entries = _entries;

        // Uno switch rimasto a meta' (anche da una sessione precedente di VS) conta piu' del riepilogo.
        if (_pendingSwitch.Get(WorkingCopyPath) is { } pending)
            return Loc.T("Errore: lo switch a {0} non è completo. Non fare Revert: premi \"Completa switch\" (cleanup + switch).", pending.Name);

        if (entries.Count == 0)
            return Loc.T("Nessuna modifica.");

        var parts = new List<string>();
        var conflicts = entries.Count(e => e.Status == SvnStatusKind.Conflicted);
        var unversioned = entries.Count(e => e.Status == SvnStatusKind.Unversioned);
        var lockedOnly = entries.Count(e => e.Status == SvnStatusKind.Normal);
        var changes = entries.Count - conflicts - unversioned - lockedOnly;
        if (conflicts > 0)
            parts.Add(Loc.T("{0} in conflitto", conflicts));
        if (changes > 0)
            parts.Add(Loc.T("{0} modificati", changes));
        if (unversioned > 0)
            parts.Add(Loc.T("{0} non versionati", unversioned));
        if (lockedOnly > 0)
            parts.Add(Loc.T("{0} bloccati", lockedOnly));
        return string.Join(", ", parts) + ".";
    }

    // ── Chiavi di riga ───────────────────────────────────────────────

    /// <summary>Elementi a cui si riferisce una chiave di riga, gia' rivalidata contro lo stato corrente.</summary>
    /// <param name="Section">Sezione della chiave.</param>
    /// <param name="WholeSection"><c>true</c> per <c>section:&lt;id&gt;</c>.</param>
    /// <param name="Path">Percorso relativo della chiave ("" per una sezione intera).</param>
    /// <param name="Entries">Elementi coinvolti (uno per un file; i discendenti per cartelle e sezioni).</param>
    private sealed record KeyTarget(ChangeSection Section, bool WholeSection, string Path, List<ChangeEntry> Entries)
    {
        /// <summary>Il file singolo (non cartella) a cui punta la chiave, oppure <c>null</c>.</summary>
        public ChangeEntry? SingleFile => !WholeSection && Entries.Count == 1 && !Entries[0].IsDirectory
            && string.Equals(Entries[0].RelativePath, Path, StringComparison.OrdinalIgnoreCase) ? Entries[0] : null;
    }

    private static ChangeSection ParseSectionId(string id) => id switch
    {
        "staged" => ChangeSection.Staged,
        "changes" => ChangeSection.Changes,
        "unversioned" => ChangeSection.Unversioned,
        _ => throw new ArgumentException(Loc.T("Sezione sconosciuta: {0}", id)),
    };

    /// <summary>
    /// Risolve <c>"&lt;sezione&gt;:&lt;percorso&gt;"</c> o <c>"section:&lt;id&gt;"</c>. La chiave dev'essere ancora presente
    /// nello stato corrente (la lista potrebbe essere cambiata dopo l'invio): altrimenti errore. Il percorso della chiave
    /// serve solo a cercare tra gli elementi di <c>svn status</c>, non viene mai usato sul file system.
    /// </summary>
    private KeyTarget ResolveKey(string key)
    {
        var colon = key.IndexOf(':');
        if (colon <= 0)
            throw new ArgumentException(Loc.T("Chiave di riga non valida."));

        var prefix = key[..colon];
        var rest = key[(colon + 1)..];
        lock (_stateLock)
        {
            if (prefix == "section")
            {
                var wholeSection = ParseSectionId(rest);
                return new KeyTarget(wholeSection, true, string.Empty, _entries.Where(e => SectionOf(e) == wholeSection).ToList());
            }

            var section = ParseSectionId(prefix);
            var path = rest.TrimEnd('/');
            var entries = _entries
                .Where(e => SectionOf(e) == section
                            && (string.Equals(e.RelativePath, path, StringComparison.OrdinalIgnoreCase)
                                || e.RelativePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (path.Length == 0 || entries.Count == 0)
                throw new ArgumentException(Loc.T("Elemento non più presente: aggiorna l'elenco (Refresh) e riprova."));

            return new KeyTarget(section, false, path, entries);
        }
    }

    // ── Staged ───────────────────────────────────────────────────────

    private void SetStaged(string key, bool staged)
    {
        lock (_stateLock)
        {
            var target = ResolveKey(key);
            var staging = Wc.Staged;
            foreach (var entry in target.Entries)
            {
                if (!staged)
                    staging.Remove(entry.FullPath);
                else if (entry.Status is not (SvnStatusKind.Conflicted or SvnStatusKind.Normal))
                    staging.Add(entry.FullPath);
            }
        }

        NotifyChanged();
    }

    // ── Albero: espandi / comprimi ───────────────────────────────────

    private IEnumerable<string> AllFolderKeys()
    {
        foreach (var section in SectionOrder)
        {
            var items = _entries.Where(e => SectionOf(e) == section).ToList();
            foreach (var key in ChangesTreeBuilder.FolderKeys(ChangesTreeBuilder.BuildTree(section, items)))
                yield return key;
        }
    }

    private void ToggleFolder(string key)
    {
        lock (_stateLock)
        {
            if (!AllFolderKeys().Contains(key, StringComparer.Ordinal))
                throw new ArgumentException(Loc.T("Cartella non più presente: aggiorna l'elenco (Refresh) e riprova."));

            ToggleKey(key);
        }
    }

    private void ToggleKey(string key)
    {
        lock (_stateLock)
        {
            if (!_collapsed.Remove(key))
                _collapsed.Add(key);
        }

        NotifyChanged();
    }

    private void SetAllExpanded(bool expanded)
    {
        lock (_stateLock)
        {
            _collapsed.Clear();
            if (!expanded)
                _collapsed.UnionWith(AllFolderKeys());
        }

        NotifyChanged();
    }

    // ── Esecuzione esclusiva ─────────────────────────────────────────

    /// <summary>
    /// Esegue <paramref name="body"/> solo se nessun'altra operazione e' in corso (altrimenti ignora la richiesta:
    /// doppio click). Aspetta un eventuale refresh gia' avviato, per non sovrapporre status e comando svn.
    /// </summary>
    internal async Task RunExclusiveAsync(Func<Task> body)
    {
        if (Interlocked.CompareExchange(ref _operationRunning, 1, 0) != 0)
        {
            SetStatus(Loc.T("Operazione in corso..."));
            return;
        }

        try
        {
            await _refreshGate.WaitAsync();
            _refreshGate.Release();
            await body();
        }
        finally
        {
            Volatile.Write(ref _operationRunning, 0);
        }
    }

    // ── Commit e update ──────────────────────────────────────────────

    /// <summary>
    /// Elementi del prossimo commit, come Git: quelli in Staged; se Staged e' vuoto tutte le modifiche dei file
    /// versionati (M, A, D, R, mancanti, conflitti che poi bloccano), mai i non versionati (come <c>git commit -a</c>).
    /// </summary>
    private (List<ChangeEntry> Entries, bool All) CommitSet()
    {
        lock (_stateLock)
        {
            var staged = _entries.Where(e => Wc.Staged.Contains(e.FullPath)).ToList();
            if (staged.Count > 0)
                return (staged, false);

            var versioned = _entries.Where(e => e.Status is SvnStatusKind.Modified or SvnStatusKind.Added or SvnStatusKind.Deleted
                or SvnStatusKind.Replaced or SvnStatusKind.Missing or SvnStatusKind.Conflicted).ToList();
            return (versioned, true); // Staged vuoto = Commit All, anche con soli non versionati (la domanda li propone)
        }
    }

    /// <summary>
    /// Commit degli elementi di <see cref="CommitSet"/>. Come TortoiseSVN: i non versionati in Staged vengono prima
    /// aggiunti e i mancanti prima cancellati (svn commit da solo li rifiuterebbe).
    /// </summary>
    private async Task CommitCoreAsync(CancellationToken ct, bool addNew = false)
    {
        var (selected, all) = CommitSet();
        List<ChangeEntry> allEntries;
        lock (_stateLock)
            allEntries = _entries;

        // Commit All con "Si', aggiungi": anche i non versionati ('?'), che piu' sotto vengono aggiunti con svn add.
        if (addNew && all)
            selected = selected.Concat(allEntries.Where(e => e.Status == SvnStatusKind.Unversioned)).ToList();

        if (selected.Count == 0)
        {
            SetStatus(Loc.T("Nessuna modifica da committare."));
            return;
        }

        if (string.IsNullOrWhiteSpace(CommitMessage.Text))
        {
            SetStatus(Loc.T("Scrivi un messaggio di commit."));
            return;
        }

        var conflicted = selected.Where(e => e.Status == SvnStatusKind.Conflicted).ToList();
        if (conflicted.Count > 0)
        {
            SetStatus(Loc.T("Risolvi prima i conflitti ({0} file): svn non committa file in conflitto.", conflicted.Count));
            return;
        }

        var toAddCount = 0;
        try
        {
            SetStatus(Loc.T("Commit in corso..."));

            // Come TortoiseSVN: le cartelle padre nuove (aggiunte o non versionate) di un elemento in Staged vanno
            // nel commit anche se non ci sono, altrimenti svn rifiuta ("is not known to exist in the repository
            // and is not part of the commit, yet its child is part of the commit").
            var parents = NewParentFolders(selected, allEntries);
            var commitEntries = parents.Concat(selected).ToList();

            var toAdd = commitEntries.Where(e => e.Status == SvnStatusKind.Unversioned).Select(e => e.FullPath).ToList();
            toAddCount = toAdd.Count;
            if (toAdd.Count > 0)
                await _svnService.AddAsync(toAdd, ct);

            var toDelete = commitEntries.Where(e => e.Status == SvnStatusKind.Missing).Select(e => e.FullPath).ToList();
            if (toDelete.Count > 0)
                await _svnService.DeleteAsync(toDelete, ct);

            // Una cartella non versionata in Staged e' una riga sola ma contiene un albero: "svn add" lo aggiunge
            // tutto, quindi nel commit entra per intero (le sue sottovoci non sono elencate singolarmente).
            var wholeFolders = commitEntries
                .Where(e => e.Status == SvnStatusKind.Unversioned && e.IsDirectory).Select(e => e.FullPath).ToList();

            var paths = commitEntries.Select(e => e.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var message = CommitMessage.Text.Trim();
            var revision = await _svnService.CommitAsync(paths, message, ct, wholeFolders);

            string? repo;
            lock (_stateLock)
            {
                repo = _repoRoot;
                _lastCommitWc = _workingCopyPath;
                _lastCommitRev = revision;
            }

            if (!string.IsNullOrEmpty(repo))
                Recent?.Add(repo, message);
            CommitMessage.Clear();
            ClearMergeBanner();

            await RefreshAsync(ct, reloadBranch: true, revision is { } rev
                ? Loc.T("Commit completato: revisione {0} ({1} elementi).", rev, selected.Count)
                : Loc.T("Commit completato: {0} elementi.", selected.Count));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Add/delete gia' eseguiti prima del commit fallito (es. "fai prima Update"): la lista va riallineata.
            var note = toAddCount > 0 ? " " + Loc.T("I file nuovi restano aggiunti (A): rifai il commit o annullali con Revert.") : string.Empty;
            await RefreshAsync(ct, reloadBranch: false, Loc.T("Errore commit: {0}", ex.Message) + note);
        }
    }

    /// <summary>
    /// Cartelle aggiunte (o non versionate) che contengono un elemento in Staged ma non ci sono esse stesse,
    /// dalla piu' esterna alla piu' interna.
    /// </summary>
    private List<ChangeEntry> NewParentFolders(IReadOnlyList<ChangeEntry> selected, List<ChangeEntry> all)
    {
        var chosen = new HashSet<string>(selected.Select(e => e.FullPath), StringComparer.OrdinalIgnoreCase);
        var byPath = all.ToDictionary(e => e.FullPath, StringComparer.OrdinalIgnoreCase);
        var root = WorkingCopyPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var found = new Dictionary<string, ChangeEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in selected)
        {
            var parent = Path.GetDirectoryName(entry.FullPath);
            while (!string.IsNullOrEmpty(parent) && parent.Length > root.Length
                   && parent.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                if (!chosen.Contains(parent) && byPath.TryGetValue(parent, out var folder)
                    && folder.Status is SvnStatusKind.Added or SvnStatusKind.Unversioned or SvnStatusKind.Replaced)
                    found[parent] = folder;

                parent = Path.GetDirectoryName(parent);
            }
        }

        return found.Values.OrderBy(e => e.FullPath.Length).ToList();
    }

    private async Task UpdateCoreAsync(CancellationToken ct)
    {
        var wc = WorkingCopyPath;
        if (string.IsNullOrWhiteSpace(wc))
        {
            SetStatus(Loc.T("Nessuna working copy configurata."));
            return;
        }

        try
        {
            SetStatus(Loc.T("Update in corso..."));
            var ot = Progress.Begin("update", "SVN Update", Loc.T("Update in corso..."), ct);
            var update = await Streaming(() => _svnService.UpdateAsync(wc, ot));
            var updated = update.Revision is { } rev ? Loc.T("Aggiornato alla revisione {0}", rev) : Loc.T("Update completato");
            await RefreshAsync(ct, reloadBranch: true, update.ConflictedPaths.Count > 0
                ? Loc.T("{0}: {1} file in conflitto.", updated, update.ConflictedPaths.Count)
                : $"{updated}.");
            Progress.Done(true, update.ConflictedPaths.Count > 0 ? Loc.T("{0}: completato con {1} conflitti.", updated, update.ConflictedPaths.Count) : $"{updated}.", update.ConflictedPaths.Count);
            await RevealConflictsAsync(update.ConflictedPaths.Count, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && Progress.IsCancelling)
        {
            await FinishCancelledAsync(wc, Loc.T("Update annullato"), [new(Loc.T("Riprova update"), "update"), new(Loc.T("Cleanup…"), "cleanup")], ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore update: {0}", ex.Message));
            Progress.Done(false, _statusMessage);
        }
    }

    internal Task FinishMergeCancelledAsync(string wc, CancellationToken ct)
        => FinishCancelledAsync(wc, Loc.T("Merge annullato"),
            [new(Loc.T("Revert delle modifiche del merge"), "progressRevertMerge"), new(Loc.T("Lascia così"), "progressClose")], ct, cleanup: false);

    /// <summary>"Revert delle modifiche del merge" dalla finestra di un merge annullato: chiude la finestra e fa il solito "Annulla merge" (con conferma).</summary>
    internal async Task RevertCancelledMergeAsync(CancellationToken ct)
    {
        Progress.Close();
        await RunExclusiveAsync(() => UndoMergeCoreAsync(ct));
    }

    /// <summary>
    /// Dopo "Annulla" su switch/update/merge: svn e' stato terminato, la working copy puo' essere a meta' o sporca. Per switch e update
    /// fa un cleanup normale (come se l'utente lo lanciasse); poi la finestra resta con l'avviso, il riepilogo e i passi proposti
    /// (tra cui "Cleanup..." con le opzioni). Il merge non fa cleanup: si decide tra revert delle modifiche e "Lascia cosi'".
    /// </summary>
    private async Task FinishCancelledAsync(string wc, string what, IReadOnlyList<ProgressActionDto> actions, CancellationToken ct, bool cleanup = true)
    {
        var failed = string.Empty;
        if (cleanup)
        {
            Progress.SetPhase(Loc.T("Cleanup in corso..."));
            try { await Streaming(async () => { await _svnService.CleanupAsync(wc, CleanupOptions.Default, ct); return true; }); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                BackendLog.Warn("Cleanup dopo annullamento non riuscito: " + ex.Message);
                failed = " " + Loc.T("Il cleanup automatico non è riuscito: {0}", ex.Message);
            }
        }

        await RefreshAsync(ct, reloadBranch: true);
        var summary = (cleanup
            ? Loc.T("{0} dopo {1} elementi già applicati. La working copy può essere rimasta a metà o sporca: è stato eseguito un cleanup normale. Scegli come proseguire, oppure apri il Cleanup con le opzioni.", what, Progress.Processed)
            : Loc.T("{0} dopo {1} elementi già applicati: restano modifiche parziali nella working copy. Puoi annullarle con un revert oppure lasciarle così.", what, Progress.Processed)) + failed;
        SetStatus(Loc.T("Errore: {0}", summary));
        Progress.Done(false, summary, cancelled: true, actions: actions);
    }

    /// <summary>Apre la scelta guidata della revisione per i percorsi indicati (working copy corrente o selezione di Solution Explorer).</summary>
    internal async Task OpenRevisionPickerAsync(IReadOnlyList<string> targets, CancellationToken ct)
    {
        if (targets.Count == 0 || string.IsNullOrWhiteSpace(targets[0]))
        {
            SetStatus(Loc.T("Nessuna working copy configurata."));
            return;
        }

        await RevPicker.OpenAsync(targets, ct);
    }

    /// <summary>OK nella scelta: revisione non valida = errore nella finestra (resta aperta), altrimenti chiude ed esegue <c>svn update -r</c>.</summary>
    private async Task RevPickerOkAsync(string typed, CancellationToken ct)
    {
        if (!RevPicker.IsOpen)
            return;
        if (!SvnRevision.TryNormalize(typed, out var revision))
        {
            RevPicker.SetError(Loc.T("\"{0}\" non è una revisione valida: scrivi un numero intero positivo (es. 1234) oppure HEAD.", typed.Trim()));
            return;
        }

        var targets = RevPicker.Targets;
        RevPicker.Close();
        await RunExclusiveAsync(() => UpdateToRevisionCoreAsync(targets, revision, ct));
    }

    /// <summary><c>svn update -r</c> sui percorsi scelti.</summary>
    private async Task UpdateToRevisionCoreAsync(IReadOnlyList<string> targets, string revision, CancellationToken ct)
    {
        try
        {
            SetStatus(Loc.T("Update alla revisione {0} in corso...", revision));
            UpdateResult? last = null;
            var conflicts = 0;
            foreach (var target in targets)
            {
                last = await _svnService.UpdateToRevisionAsync(target, revision, ct);
                conflicts += last.ConflictedPaths.Count;
            }

            var updated = last?.Revision is { } rev ? Loc.T("Aggiornato alla revisione {0}", rev) : Loc.T("Update completato");
            await RefreshAsync(ct, reloadBranch: true, conflicts > 0
                ? Loc.T("{0}: {1} file in conflitto.", updated, conflicts)
                : $"{updated}.");
            await RevealConflictsAsync(conflicts, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore update: {0}", ex.Message));
        }
    }

    /// <summary>Ramo corrente (dall'URL di <c>svn info</c> e dal layout del repository) e versione di svn. Un errore non blocca lo stato: ramo vuoto ("—").</summary>
    private async Task<BranchInfo> LoadBranchAsync(string path, CancellationToken ct)
    {
        var label = string.Empty;
        string? url = null, root = null;
        long? rev = null;
        try
        {
            var info = await _svnService.GetInfoAsync(path, ct);
            label = RepoLayout.Parse(await _settingsService.GetRepoLayoutAsync(ct)).Deduce(info.RelativeUrl).Label;
            (url, root, rev) = (info.Url, info.RepositoryRoot, info.Revision);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BackendLog.Warn("svn info fallito: " + ex.Message);
        }

        var version = await _svnService.GetSvnVersionAsync(ct);
        return new BranchInfo(label, version?.ToString(), url, root, rev);
    }

    /// <summary>Ramo corrente, versione di svn e dati di <c>svn info</c> (URL, radice del repository, revisione della working copy).</summary>
    private sealed record BranchInfo(string Label, string? Version, string? Url, string? Root, long? Rev);

    /// <summary>OK nel selettore: l'URL viene solo dall'elenco (mai dalla UI); con modifiche locali chiede conferma, poi <c>svn switch</c>.</summary>
    private async Task SwitchOkAsync(string name, CancellationToken ct)
    {
        if (!Switch.IsOpen)
            return;
        var target = Switch.Find(name);
        if (target is null)
        {
            Switch.SetError(Loc.T("Elemento non valido: aggiorna l'elenco e riprova."));
            return;
        }

        if (Switch.Mode == "merge")
        {
            // Sorgente scelta: il resto (revisioni, anteprima, merge) sta nel Log in modalita' merge.
            Switch.RememberUsed(target.Name);
            Switch.Close();
            if (StartMerge is not null)
                await StartMerge(WorkingCopyPath, target, ct);
            return;
        }

        if (target.Current)
        {
            Switch.SetError(Loc.T("La working copy è già su {0}.", target.Name));
            return;
        }

        // Completare uno switch interrotto non porta altrove le modifiche: le cartelle "incomplete" non sono lavoro dell'utente.
        // Working copy sporca: prima dello switch l'avviso con il riepilogo (continua / Cleanup / annulla).
        int dirty;
        lock (_stateLock)
            dirty = target.Incomplete ? 0 : Total(CountDirty(_entries.Select(e => e.Status)));
        if (dirty > 0)
        {
            Switch.Close();
            _warnTarget = target;
            NotifyChanged();
            return;
        }

        await StartSwitchAsync(target, ct);
    }

    private async Task StartSwitchAsync(SwitchPickerStore.Target target, CancellationToken ct)
    {
        Switch.RememberUsed(target.Name);
        Switch.Close();
        await RunExclusiveAsync(() => SwitchCoreAsync(target, ct));
    }

    /// <summary>Attesa prima di riprovare uno switch interrotto (un antivirus o VS che tiene aperto un file di solito lo rilascia in fretta).</summary>
    internal TimeSpan SwitchRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// <c>svn switch</c>. Prima di partire segna lo switch come "in corso" (<see cref="PendingSwitch"/>): se svn si ferma a meta'
    /// (es. E155009, cartella tenuta aperta da un altro programma) la working copy resta mista e il segno resta, con "Completa switch".
    /// Su un errore da cleanup ci riprova da solo una volta (cleanup + stesso switch, che svn riprende da dove era rimasto).
    /// </summary>
    private async Task SwitchCoreAsync(SwitchPickerStore.Target target, CancellationToken ct, long? backToRevision = null)
    {
        var wc = WorkingCopyPath;
        var existing = _pendingSwitch.Get(wc);
        var resuming = existing is not null;
        if (backToRevision is null) // "Torna a prima" lascia la voce com'e': si toglie solo se finisce bene
        {
            // Punto di partenza (per "Torna a prima dello switch"): lo switch che riprende conserva quello gia' salvato.
            var from = existing;
            if (from is null)
            {
                try { var info = await _svnService.GetInfoAsync(wc, ct); from = new PendingSwitch.Entry(string.Empty, info.Url, info.Url, info.Revision, _branchName); }
                catch (Exception ex) when (ex is not OperationCanceledException) { BackendLog.Warn("Punto di partenza dello switch non letto: " + ex.Message); }
            }

            _pendingSwitch.Set(wc, new PendingSwitch.Entry(target.Name, target.Url, from?.FromUrl, from?.FromRev, from?.FromName));
        }

        var ot = Progress.Begin("switch", Loc.T("Switch a {0}", target.Name), Loc.T("Verifica working copy"), ct);
        try
        {
            if (resuming)
            {
                SetStatus(Loc.T("Cleanup prima dello switch..."));
                Progress.SetPhase(Loc.T("Cleanup in corso..."));
                await Streaming(async () => { await _svnService.CleanupAsync(wc, CleanupOptions.Default, ct); return true; });
            }

            SetStatus(Loc.T("Switch a {0} in corso...", target.Name));
            Progress.SetPhase(Loc.T("Switch in corso..."));
            UpdateResult result;
            try
            {
                result = await Streaming(() => _svnService.SwitchAsync(wc, target.Url, ot, backToRevision));
            }
            catch (Exception first) when (!ct.IsCancellationRequested && SvnErrors.IsWorkingCopyLocked(first.Message))
            {
                BackendLog.Warn("Switch interrotto, cleanup e nuovo tentativo: " + first.Message);
                SetStatus(Loc.T("Switch a {0} interrotto: cleanup e nuovo tentativo...", target.Name));
                Progress.SetPhase(Loc.T("Switch interrotto: cleanup e nuovo tentativo..."));
                await Streaming(async () => { await _svnService.CleanupAsync(wc, CleanupOptions.Default, ct); return true; });
                if (SwitchRetryDelay > TimeSpan.Zero)
                    await Task.Delay(SwitchRetryDelay, ot);
                Progress.SetPhase(Loc.T("Switch in corso..."));
                result = await Streaming(() => _svnService.SwitchAsync(wc, target.Url, ot, backToRevision));
            }

            _pendingSwitch.Clear(wc);
            var done = result.Revision is { } rev ? Loc.T("Switch a {0} completato (revisione {1})", target.Name, rev) : Loc.T("Switch a {0} completato", target.Name);
            await RefreshAsync(ct, reloadBranch: true, (result.ConflictedPaths.Count > 0
                ? Loc.T("{0}: {1} file in conflitto.", done, result.ConflictedPaths.Count)
                : $"{done}.") + " " + Loc.T("Se Visual Studio lo chiede, ricarica la solution."));
            Progress.Done(true, result.ConflictedPaths.Count > 0 ? Loc.T("{0}: completato con {1} conflitti.", done, result.ConflictedPaths.Count) : $"{done}.", result.ConflictedPaths.Count);
            await RevealConflictsAsync(result.ConflictedPaths.Count, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && Progress.IsCancelling)
        {
            // PendingSwitch resta: lo stato "Errore" qui sotto mostra "Completa switch" / "Torna a prima dello switch"; la finestra li ripete.
            List<ProgressActionDto> next = [new(Loc.T("Completa switch"), "switchResume")];
            if (_pendingSwitch.Get(wc) is { CanGoBack: true })
                next.Add(new(Loc.T("Torna a prima dello switch"), "switchBack"));
            next.Add(new(Loc.T("Cleanup…"), "cleanup"));
            await FinishCancelledAsync(wc, Loc.T("Switch a {0} annullato", target.Name), next, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BackendLog.Warn("Switch fallito: " + ex.Message);
            // Si rilegge il ramo vero (l'intestazione non deve restare sul vecchio) e si guarda se svn ha gia' toccato la working copy.
            await RefreshAsync(ct, reloadBranch: true);
            bool halfway;
            lock (_stateLock)
                halfway = SameUrl(_branchUrl, target.Url) || _entries.Any(e => e.Status == SvnStatusKind.Incomplete);
            if (halfway || resuming)
            {
                SetStatus(Loc.T("Errore: lo switch a {0} si è interrotto a metà, parte della working copy è ancora sul ramo di prima. Non fare Revert: chiudi ciò che tiene aperti i file citati da svn (es. la solution) e premi \"Completa switch\" (cleanup + switch).\n{1}", target.Name, ex.Message));
            }
            else
            {
                _pendingSwitch.Clear(wc); // svn non ha cambiato nulla (es. URL o credenziali): niente da completare
                SetStatus(Loc.T("Errore switch: {0}", ex.Message));
            }

            Progress.Done(false, _statusMessage);
        }
    }

    /// <summary>"Completa switch": cleanup + lo stesso <c>svn switch</c> verso la destinazione dello switch interrotto.</summary>
    private Task ResumeSwitchCoreAsync(CancellationToken ct)
    {
        var pending = _pendingSwitch.Get(WorkingCopyPath);
        if (pending is null)
        {
            SetStatus(Loc.T("Nessuno switch da completare."));
            return Task.CompletedTask;
        }

        return SwitchCoreAsync(new SwitchPickerStore.Target(pending.Name, string.Empty, pending.Url, false, string.Empty, Incomplete: true), ct);
    }

    /// <summary>"Torna a prima dello switch": cleanup normale + <c>svn switch -r</c> verso URL e revisione di partenza (le modifiche locali restano).</summary>
    private Task SwitchBackCoreAsync(CancellationToken ct)
    {
        if (_pendingSwitch.Get(WorkingCopyPath) is not { CanGoBack: true } p)
        {
            SetStatus(Loc.T("Nessuno switch da annullare."));
            return Task.CompletedTask;
        }

        var name = string.IsNullOrEmpty(p.FromName) ? p.FromUrl! : p.FromName;
        return SwitchCoreAsync(new SwitchPickerStore.Target(name, string.Empty, p.FromUrl!, false, string.Empty, Incomplete: true), ct, p.FromRev);
    }

    /// <summary>Stesso URL a meno di codifica (%20) e barra finale: svn info e l'elenco dei rami codificano in modo diverso.</summary>
    private static bool SameUrl(string? a, string? b)
        => a is not null && b is not null
           && string.Equals(Uri.UnescapeDataString(a).TrimEnd('/'), Uri.UnescapeDataString(b).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// "Annulla merge": revert ricorsivo della working copy (era pulita quando il merge e' partito, quindi non porta via altro lavoro).
    /// Conferma elencando i file. I file aggiunti dal merge (con storia) li rimuove svn stesso; gli altri aggiunti restano su disco non versionati.
    /// </summary>
    private async Task UndoMergeCoreAsync(CancellationToken ct)
    {
        var wc = WorkingCopyPath;
        List<ChangeEntry> entries;
        lock (_stateLock)
            entries = _entries.Where(e => StatusPresentation.IsRevertable(e.Status)).ToList();
        if (string.IsNullOrWhiteSpace(wc) || entries.Count == 0)
        {
            ClearMergeBanner();
            SetStatus(Loc.T("Nessun elemento idoneo per SVN Revert."));
            return;
        }

        if (!await _dialogs.ConfirmAsync(Loc.T("Annullare il merge? Tutte le modifiche locali della working copy ({0} elementi) tornano allo stato di BASE.\n\n{1}\n\nL'operazione non è annullabile.",
                entries.Count, ListForConfirmation(entries)), ct))
            return;

        try
        {
            SetStatus(Loc.T("Annullamento del merge in corso..."));
            await _svnService.RevertAsync([wc], ct, recursive: true);
            ClearMergeBanner();
            await RefreshAsync(ct, reloadBranch: false, Loc.T("Merge annullato: {0} elementi ripristinati.", entries.Count));
            if (MergeUndone is not null)
                await MergeUndone(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore revert: {0}", ex.Message));
        }
    }

    /// <summary>"SVN Revert di tutto" (avviso del Log in modalita' merge): annulla ogni modifica locale (i non versionati no), con conferma e elenco.</summary>
    internal async Task RevertAllAsync(CancellationToken ct)
    {
        await RefreshAsync(ct); // l'elenco in conferma e' quello di adesso, anche se Changes non e' mai stato aperto
        await RunExclusiveAsync(() =>
        {
            List<ChangeEntry> all;
            lock (_stateLock)
                all = _entries.Where(e => SectionOf(e) != ChangeSection.Unversioned).ToList();
            return RevertCoreAsync(new KeyTarget(ChangeSection.Changes, true, string.Empty, all), ct, everything: true);
        });
    }

    /// <summary>SVN Update della working copy (da un errore che lo suggerisce, es. revisioni miste).</summary>
    internal Task UpdateNowAsync(CancellationToken ct) => RunExclusiveAsync(() => UpdateCoreAsync(ct));

    /// <summary>SVN Cleanup della working copy (rimuove i lock lasciati da operazioni interrotte).</summary>
    private void OpenCleanupDialog()
    {
        _cleanupOpen = true;
        NotifyChanged();
    }

    /// <summary>Chiude la finestra delle opzioni; <paramref name="back"/> = annullata: se veniva dall'avviso dello switch, torna l'avviso.</summary>
    private void CloseCleanupDialog(bool back)
    {
        _cleanupOpen = false;
        if (back && _cleanupReturn is { } t)
        {
            _cleanupReturn = null;
            _warnTarget = t;
        }

        NotifyChanged();
    }

    /// <summary>Opzioni dalla UI: le distruttive (elimina non versionati/ignorati, revert) senza <c>confirmed</c> si rifiutano anche se la UI le ha lasciate passare.</summary>
    internal static CleanupOptions ParseCleanupOptions(JsonElement args, Version? svn)
    {
        var o = new CleanupOptions(Args.Bool(args, "status"), Args.Bool(args, "vacuum"), Args.Bool(args, "externals"),
            Args.Bool(args, "unversioned"), Args.Bool(args, "ignored"), Args.Bool(args, "revert"));
        if (!o.RunsCleanup && !o.RevertAll)
            throw new ArgumentException(Loc.T("Scegli almeno un'opzione del Cleanup."));
        if (o.UsesExtras && !CleanupOptions.ExtrasSupported(svn))
            throw new ArgumentException(Loc.T("Queste opzioni del Cleanup richiedono svn 1.9 o successivo."));
        if (o.Destructive && !Args.Bool(args, "confirmed"))
            throw new ArgumentException(Loc.T("Le opzioni che eliminano file o annullano modifiche richiedono la conferma esplicita."));
        return o;
    }

    private async Task CleanupOkAsync(JsonElement args, CancellationToken ct)
    {
        if (!_cleanupOpen)
            return;
        var options = ParseCleanupOptions(args, SvnVersionParsed); // se non valide la finestra resta aperta (errore come toast)
        _cleanupOpen = false;
        NotifyChanged();
        await RunExclusiveAsync(() => CleanupCoreAsync(options, ct));
    }

    /// <summary>SVN Cleanup della working copy con le opzioni scelte (rimuove i lock lasciati da operazioni interrotte).</summary>
    private async Task CleanupCoreAsync(CleanupOptions options, CancellationToken ct)
    {
        var wc = WorkingCopyPath;
        if (string.IsNullOrWhiteSpace(wc))
        {
            SetStatus(Loc.T("Nessuna working copy configurata."));
            return;
        }

        try
        {
            SetStatus(Loc.T("Cleanup in corso..."));
            Progress.Begin("cleanup", "SVN Cleanup", Loc.T("Cleanup in corso..."));
            await Streaming(async () => { await _svnService.CleanupAsync(wc, options, ct); return true; });
            await RefreshAsync(ct, reloadBranch: false, Loc.T("Cleanup completato."));
            Progress.Done(true, Loc.T("Cleanup completato."));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore cleanup: {0}", ex.Message));
            Progress.Done(false, _statusMessage);
        }
    }

    /// <summary>Messaggio di stato + eventuale pulsante d'azione: working copy bloccata -> "Esegui Cleanup".</summary>
    private StatusDto BuildStatus()
    {
        // "Errore..." (it) e "Error..." (en) iniziano uguale: il test non dipende dalla lingua.
        var isError = _statusMessage.StartsWith("Error", StringComparison.Ordinal);
        return new StatusDto(_statusMessage, isError,
            !isError ? null
                : _pendingSwitch.Get(_workingCopyPath) is not null ? new StatusActionDto(Loc.T("Completa switch"), "switchResume")
                : SvnErrors.IsWorkingCopyLocked(_statusMessage) ? new StatusActionDto(Loc.T("Esegui Cleanup"), "cleanup")
                : SvnErrors.SuggestsUpdate(_statusMessage) ? new StatusActionDto(Loc.T("SVN Update ora"), "update") : null, _statusSeq,
            isError && _pendingSwitch.Get(_workingCopyPath) is { CanGoBack: true } ? new StatusActionDto(Loc.T("Torna a prima dello switch"), "switchBack") : null);
    }

    // ── Operazioni su file, cartelle e sezioni ───────────────────────

    private Task RowOpAsync(string op, string key, CancellationToken ct)
    {
        // La chiave si risolve dentro l'esecuzione esclusiva: dopo l'attesa di un refresh in corso lo stato potrebbe essere cambiato.
        return RunExclusiveAsync(() =>
        {
            var target = ResolveKey(key);
            return op switch
            {
                "add" => AddCoreAsync(target, ct),
                "deleteFile" => DeleteFileCoreAsync(target, ct),
                "revert" => RevertCoreAsync(target, ct),
                "resolve" => ResolveCoreAsync(target, ct),
                "ignore" => IgnoreCoreAsync(target, ct),
                "lock" => LockCoreAsync(target, ct),
                "unlock" => UnlockCoreAsync(target, ct),
                _ => throw new ArgumentException(Loc.T("Operazione sconosciuta: {0}", op)),
            };
        });
    }

    /// <summary>SVN Add sugli elementi non versionati della riga; restano in Staged (chi aggiunge di solito vuole committare).</summary>
    private async Task AddCoreAsync(KeyTarget target, CancellationToken ct)
    {
        var affected = target.Entries.Where(e => e.Status == SvnStatusKind.Unversioned).ToList();
        if (affected.Count == 0)
        {
            SetStatus(Loc.T("Nessun elemento non versionato da aggiungere."));
            return;
        }

        try
        {
            SetStatus(Loc.T("Aggiunta a SVN in corso..."));
            await _svnService.AddAsync(affected.Select(e => e.FullPath).ToList(), ct);

            lock (_stateLock)
            {
                foreach (var entry in affected)
                    Wc.Staged.Add(entry.FullPath);
            }

            await RefreshAsync(ct, reloadBranch: false, Loc.T("Aggiunti a SVN: {0} elementi.", affected.Count));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore add: {0}", ex.Message));
        }
    }

    /// <summary>
    /// Tasto Canc su un file o una cartella (gli elementi mostrati sotto la riga; non una sezione intera), dopo conferma con elenco:
    /// versionati <c>svn delete --force</c> (resta da committare), non versionati nel cestino (cartelle comprese, ricorsivo).
    /// </summary>
    private async Task DeleteFileCoreAsync(KeyTarget target, CancellationToken ct)
    {
        var items = target.WholeSection ? [] : target.Entries.Where(CanDeleteFile).ToList();
        items = items.Where(e => !items.Any(o => o.IsDirectory && IsUnder(e.FullPath, o.FullPath))).ToList(); // svn e disco cancellano gia' il sottoalbero
        if (items.Count == 0)
        {
            SetStatus(Loc.T("Nessun file idoneo per l'eliminazione."));
            return;
        }

        // Difesa in profondita': il file viene da svn status, ma non si tocca mai nulla fuori dalla working copy.
        if (items.Any(e => !IsUnderWorkingCopy(e.FullPath)))
        {
            SetStatus(Loc.T("Errore: il file non si trova nella working copy."));
            return;
        }

        var versioned = items.Where(e => e.Status != SvnStatusKind.Unversioned).ToList();
        var loose = items.Where(e => e.Status == SvnStatusKind.Unversioned).ToList();
        var message = items.Count == 1 && !items[0].IsDirectory
            ? (versioned.Count == 1
                ? Loc.T("Eliminare \"{0}\"?\n\nIl file verrà eliminato dalla cartella di lavoro e rimosso da SVN al prossimo commit.", items[0].RelativePath)
                : Loc.T("Eliminare \"{0}\" dal disco?\n\nIl file non è versionato: verrà spostato nel cestino.", items[0].RelativePath))
            : Loc.T("Eliminare {0} elementi?\n\n{1}\n\nI versionati ({2}) verranno rimossi da SVN al prossimo commit; i non versionati ({3}) andranno nel cestino, cartelle comprese.",
                items.Count, ListForConfirmation(items), versioned.Count, loose.Count);
        if (items.Any(e => e.Status is SvnStatusKind.Modified or SvnStatusKind.Replaced or SvnStatusKind.Conflicted or SvnStatusKind.Added))
            message += "\n\n" + Loc.T("ATTENZIONE: il file ha modifiche locali non committate, che andranno perse.");

        if (!await _dialogs.ConfirmAsync(message, ct))
            return;

        try
        {
            SetStatus(Loc.T("Eliminazione in corso..."));
            if (versioned.Count > 0)
                await _svnService.DeleteAsync(versioned.Select(e => e.FullPath).ToList(), ct, force: true);
            foreach (var e in loose)
                DeleteFromDisk(e.FullPath);

            await RefreshAsync(ct, reloadBranch: false,
                items.Count == 1 ? Loc.T("Eliminato: {0}.", items[0].RelativePath) : Loc.T("Eliminati {0} elementi.", items.Count));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore eliminazione: {0}", ex.Message));
        }
    }

    private bool IsUnderWorkingCopy(string fullPath)
    {
        var root = WorkingCopyPath.TrimEnd('\\', '/');
        return root.Length > 0 && IsUnder(Path.GetFullPath(fullPath), root);
    }

    private static void RecycleOrDelete(string path)
    {
        var dir = Directory.Exists(path);
        try
        {
            if (dir)
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && (dir ? Directory.Exists(path) : File.Exists(path)))
        {
            // Cestino non disponibile (es. unita' di rete): eliminazione definitiva, l'utente ha gia' confermato.
            BackendLog.Warn("Cestino non disponibile, eliminazione diretta: " + ex.Message);
            if (dir) Directory.Delete(path, recursive: true); else File.Delete(path);
        }
    }

    /// <summary>SVN Revert su file, cartella o sezione. Chiede conferma elencando cosa verra' annullato.</summary>
    private async Task RevertCoreAsync(KeyTarget target, CancellationToken ct, bool everything = false)
    {
        // Percorsi espliciti: si annulla esattamente cio' che la conferma elenca (vedi RevertPlan).
        var targets = RevertPlan(target.Entries.Where(e => StatusPresentation.IsRevertable(e.Status)).ToList());
        if (targets.Count == 0)
        {
            SetStatus(Loc.T("Nessun elemento idoneo per SVN Revert."));
            return;
        }

        var scope = everything ? string.Empty
            : target.WholeSection
            ? $" ({ChangesTreeBuilder.SectionTitle(target.Section)})"
            : target.SingleFile is null ? Loc.T(" nella cartella \"{0}\"", target.Path) : string.Empty;
        var message = Loc.T("Annullare le modifiche locali su {0} elementi{1}?\n\n{2}\n\nLe modifiche andranno perse e l'operazione non è annullabile.",
            targets.Count, scope, ListForConfirmation(targets));
        if (!await _dialogs.ConfirmAsync(message, ct))
            return;

        try
        {
            SetStatus(Loc.T("Revert in corso..."));
            // Cartelle aggiunte/sostituite: svn le annulla solo con i figli (E155038), che sono gia' in elenco.
            // Tutto il resto, cartelle comprese (es. solo svn:ignore), a --depth empty: niente effetti sul sottoalbero.
            var recursive = targets.Where(IsNewFolder).Select(e => e.FullPath).ToList();
            var shallow = targets.Select(e => e.FullPath)
                .Where(p => !recursive.Any(r => IsUnder(p, r) || string.Equals(p, r, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (recursive.Count > 0)
                await _svnService.RevertAsync(recursive, ct, recursive: true);
            if (shallow.Count > 0)
                await _svnService.RevertAsync(shallow, ct, recursive: false);
            await RefreshAsync(ct, reloadBranch: false, Loc.T("Revert completato su {0} elementi.", targets.Count));
            bool left;
            lock (_stateLock)
                left = _entries.Any(e => StatusPresentation.IsRevertable(e.Status));
            if (!left)
                ClearMergeBanner(); // niente piu' da annullare: il banner del merge non ha piu' senso
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore revert: {0}", ex.Message));
        }
    }

    private bool IsNewFolder(ChangeEntry entry)
    {
        List<ChangeEntry> all;
        lock (_stateLock)
            all = _entries;
        return entry.Status is SvnStatusKind.Added or SvnStatusKind.Replaced
               && (entry.IsDirectory || all.Any(e => IsUnder(e.FullPath, entry.FullPath)));
    }

    private static bool IsUnder(string path, string folder)
        => path.StartsWith(folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Elementi che il revert tocca davvero: quelli richiesti piu', per le cartelle aggiunte/cancellate/mancanti,
    /// le loro voci strutturali (aggiunte/cancellate/mancanti) gia' presenti nello status.
    /// Una cartella che ha solo modifiche di proprieta' resta da sola.
    /// </summary>
    private List<ChangeEntry> RevertPlan(List<ChangeEntry> requested)
    {
        var plan = new List<ChangeEntry>(requested);
        var known = new HashSet<string>(requested.Select(e => e.FullPath), StringComparer.OrdinalIgnoreCase);
        List<ChangeEntry> all;
        lock (_stateLock)
            all = _entries;

        foreach (var folder in requested.Where(e =>
                     e.Status is SvnStatusKind.Added or SvnStatusKind.Replaced or SvnStatusKind.Deleted or SvnStatusKind.Missing))
        {
            foreach (var child in all.Where(e => IsUnder(e.FullPath, folder.FullPath)
                                                  && e.Status is SvnStatusKind.Added or SvnStatusKind.Replaced
                                                      or SvnStatusKind.Deleted or SvnStatusKind.Missing))
            {
                if (known.Add(child.FullPath))
                    plan.Add(child);
            }
        }

        return plan;
    }

    private static string ListForConfirmation(IReadOnlyList<ChangeEntry> entries)
    {
        var lines = entries.Take(MaxListedInConfirmation)
            .Select(e => $"• {e.RelativePath} ({StatusPresentation.DisplayName(e.Status, e.IsTreeConflict)})");
        var text = string.Join("\n", lines);
        return entries.Count > MaxListedInConfirmation
            ? $"{text}\n{Loc.T("… e altri {0}", entries.Count - MaxListedInConfirmation)}"
            : text;
    }

    /// <summary>
    /// Risolve un conflitto. Conflitto di testo: l'utente sceglie Working / Mine-full / Theirs-full / Base.
    /// Conflitto ad albero: non ci sono marcatori, si chiede solo conferma di segnarlo risolto.
    /// </summary>
    private async Task ResolveCoreAsync(KeyTarget target, CancellationToken ct)
    {
        if (target.SingleFile is not { Status: SvnStatusKind.Conflicted } entry)
        {
            SetStatus(Loc.T("Seleziona un file in conflitto da risolvere."));
            return;
        }

        SvnConflictChoice choice;
        if (entry.IsTreeConflict)
        {
            if (!await ConfirmTreeConflictAsync(entry, ct))
                return;
            choice = SvnConflictChoice.Working;
        }
        else
        {
            var chosen = await _dialogs.ChooseConflictResolutionAsync(entry.Name, ct);
            if (chosen is not { } picked)
                return;
            choice = picked;
        }

        await ApplyResolveAsync(entry, choice, ct);
    }

    private Task<bool> ConfirmTreeConflictAsync(ChangeEntry entry, CancellationToken ct) => _dialogs.ConfirmAsync(
        Loc.T("Conflitto ad albero su \"{0}\" (rinomina/cancellazione). " +
              "Sistema prima la struttura a mano, poi conferma per segnarlo come risolto.", entry.RelativePath), ct);

    private async Task ApplyResolveAsync(ChangeEntry entry, SvnConflictChoice choice, CancellationToken ct)
    {
        try
        {
            SetStatus(Loc.T("Risoluzione conflitto in corso..."));
            await _svnService.ResolveAsync(entry.FullPath, choice, ct);
            await RefreshAsync(ct, reloadBranch: false, Loc.T("Conflitto risolto: {0}", entry.RelativePath));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore resolve: {0}", ex.Message));
        }
    }

    /// <summary>
    /// Pulsanti della sezione Conflitti: <c>edit</c> = editor di merge a 3 vie (vsDiffMerge, poi TortoiseMerge, poi il file con i marcatori in VS),
    /// <c>mine</c> / <c>theirs</c> = tieni tutto il mio / tutto il loro (con conferma: scartano l'altra parte), <c>resolved</c> = segna come risolto (working;
    /// rifiutato se restano i marcatori). Un conflitto ad albero ammette solo <c>resolved</c>, con conferma.
    /// </summary>
    private async Task ConflictOpAsync(string op, string key, CancellationToken ct)
    {
        var target = ResolveKey(key);
        if (target.SingleFile is not { Status: SvnStatusKind.Conflicted } entry)
        {
            SetStatus(Loc.T("Seleziona un file in conflitto da risolvere."));
            return;
        }

        if (entry.IsTreeConflict && op != "resolved")
        {
            SetStatus(Loc.T("Conflitto ad albero: non ci sono versioni da scegliere. Sistema la struttura a mano, poi segnalo come risolto."));
            return;
        }

        switch (op)
        {
            case "edit":
                await OpenMergeEditorAsync(entry);
                break;
            case "mine":
                if (await _dialogs.ConfirmAsync(Loc.T("Tenere la TUA versione di \"{0}\"? Le modifiche arrivate dal server su questo file vengono scartate.", entry.RelativePath), ct))
                    await ApplyResolveAsync(entry, SvnConflictChoice.MineFull, ct);
                break;
            case "theirs":
                if (await _dialogs.ConfirmAsync(Loc.T("Prendere la versione LORO (del server) di \"{0}\"? Le TUE modifiche locali su questo file vengono scartate.", entry.RelativePath), ct))
                    await ApplyResolveAsync(entry, SvnConflictChoice.TheirsFull, ct);
                break;
            default:
                if (!entry.IsTreeConflict || await ConfirmTreeConflictAsync(entry, ct))
                    await ApplyResolveAsync(entry, SvnConflictChoice.Working, ct);
                break;
        }
    }

    /// <summary>
    /// "Segna tutti come risolti" per i conflitti ad albero (di solito si risolvono sempre tutti): conferma con l'elenco, poi
    /// <c>resolve --accept working</c> su ognuno. Un errore su uno non ferma gli altri: gli errori vengono riportati in fondo.
    /// </summary>
    private async Task ResolveTreeConflictsAsync(CancellationToken ct)
    {
        List<ChangeEntry> trees;
        lock (_stateLock)
            trees = _entries.Where(e => e.Status == SvnStatusKind.Conflicted && e.IsTreeConflict).ToList();
        if (trees.Count == 0)
        {
            SetStatus(Loc.T("Nessun conflitto ad albero da risolvere."));
            return;
        }

        if (!await _dialogs.ConfirmAsync(
                Loc.T("Segnare come risolti {0} conflitti ad albero (rinomine/cancellazioni)? Sistema prima la struttura a mano: svn non controlla nulla.\n\n{1}",
                    trees.Count, ListForConfirmation(trees)), ct))
            return;

        SetStatus(Loc.T("Risoluzione conflitti ad albero in corso..."));
        var failed = new List<string>();
        foreach (var entry in trees)
        {
            try
            {
                await _svnService.ResolveAsync(entry.FullPath, SvnConflictChoice.Working, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failed.Add($"{entry.RelativePath}: {FirstLine(ex.Message)}");
            }
        }

        var done = trees.Count - failed.Count;
        await RefreshAsync(ct, reloadBranch: false, failed.Count == 0
            ? Loc.T("Conflitti ad albero risolti: {0}.", done)
            : Loc.T("Errore resolve: {0} conflitti ad albero risolti, {1} falliti. {2}", done, failed.Count, string.Join(" | ", failed)));
    }

    private async Task OpenMergeEditorAsync(ChangeEntry entry)
    {
        SvnConflictInfo? info;
        lock (_stateLock)
            _conflictInfo.TryGetValue(entry.FullPath, out info);
        if (info is { Type: "text", Base: { } b, Mine: { } m, Theirs: { } t } && MergeEditorLauncher(new MergeEditorFiles(b, m, t, entry.FullPath)) is { } editor)
        {
            SetStatus(Loc.T("Editor di merge aperto ({0}): salva il risultato, poi premi \"Segna come risolto\".", editor));
            return;
        }

        // Nessun editor a 3 vie (o conflitto senza dati): il file con i marcatori nell'editor di VS.
        await _host.OpenFileAsync(entry.FullPath);
        SetStatus(Loc.T("Nessun editor di merge a 3 vie disponibile: aperto il file con i marcatori di conflitto (<<<<<<<, =======, >>>>>>>). Sistemalo e rimuovili, poi premi \"Segna come risolto\"."));
    }

    /// <summary>
    /// Aggiunge alla ignore list. Su una cartella o sezione (che raggruppa elementi non versionati) ignora
    /// ciascun elemento non versionato contenuto, dopo conferma.
    /// </summary>
    private async Task IgnoreCoreAsync(KeyTarget target, CancellationToken ct)
    {
        var targets = target.Entries.Where(e => e.Status == SvnStatusKind.Unversioned).ToList();
        if (targets.Count == 0)
        {
            SetStatus(Loc.T("Add to Ignore List è disponibile solo per elementi non versionati."));
            return;
        }

        if (target.SingleFile is null
            && !await _dialogs.ConfirmAsync(
                Loc.T("Aggiungere {0} elementi non versionati alla ignore list (svn:ignore) delle rispettive cartelle?\n\n{1}",
                    targets.Count, ListForConfirmation(targets)), ct))
            return;

        try
        {
            SetStatus(Loc.T("Aggiornamento ignore list in corso..."));
            foreach (var entry in targets)
                await _svnService.AddToIgnoreListAsync(entry.FullPath, ct);

            await RefreshAsync(ct, reloadBranch: false, Loc.T("Aggiunti alla ignore list: {0} elementi.", targets.Count));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore ignore list: {0}", ex.Message));
        }
    }

    /// <summary>
    /// SVN Lock su un file modificato, con messaggio facoltativo. Se è già bloccato da un altro, chiede una conferma
    /// esplicita prima di rubare il lock (<c>--force</c>).
    /// </summary>
    private async Task LockCoreAsync(KeyTarget target, CancellationToken ct)
    {
        if (target.SingleFile is not { IsLocked: false } entry || entry.Status != SvnStatusKind.Modified)
        {
            SetStatus(Loc.T("Seleziona un file modificato non ancora bloccato."));
            return;
        }

        var message = await _dialogs.AskTextAsync(Loc.T("Blocca \"{0}\" (svn lock). Messaggio facoltativo:", entry.RelativePath), Loc.T("Messaggio"), string.Empty, ct);
        if (message is null)
            return;

        try
        {
            SetStatus(Loc.T("Lock in corso..."));
            try
            {
                await _svnService.LockAsync([entry.FullPath], message, force: false, ct);
            }
            catch (InvalidOperationException ex) when (SvnErrors.IsLockedByOther(ex.Message))
            {
                if (!await _dialogs.ConfirmAsync(
                        Loc.T("\"{0}\" è già bloccato da un altro utente.\n\n{1}\n\nRubare il lock lo toglie a lui: potrebbe perdere il diritto di committare il file. Procedere?",
                            entry.RelativePath, FirstLine(ex.Message)), ct))
                {
                    SetStatus(Loc.T("Lock non preso: il file è bloccato da un altro utente."));
                    return;
                }

                await _svnService.LockAsync([entry.FullPath], message, force: true, ct);
            }

            await RefreshAsync(ct, reloadBranch: false, Loc.T("Bloccato (lock): {0}", entry.RelativePath));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore lock: {0}", ex.Message));
        }
    }

    private async Task UnlockCoreAsync(KeyTarget target, CancellationToken ct)
    {
        if (target.SingleFile is not { IsLocked: true } entry)
        {
            SetStatus(Loc.T("Seleziona un file bloccato da te."));
            return;
        }

        try
        {
            SetStatus(Loc.T("Unlock in corso..."));
            await _svnService.UnlockAsync([entry.FullPath], ct);
            await RefreshAsync(ct, reloadBranch: false, Loc.T("Sbloccato (unlock) completato: {0}", entry.RelativePath));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore unlock: {0}", ex.Message));
        }
    }

    private static string FirstLine(string text) => text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;

    // ── Diff (si apre nell'editor di Visual Studio) ──────────────────

    /// <summary>
    /// Diff di un file: esporta BASE in un file temporaneo (<c>svn export -r BASE</c>) e chiede all'host di aprire il
    /// confronto BASE ⇄ working copy nell'editor, come Git Changes.
    /// </summary>
    private async Task ShowDiffAsync(string key, CancellationToken ct)
    {
        var target = ResolveKey(key);
        // Come Git Changes: un file nuovo (A) o non versionato (?) non ha una versione precedente, quindi si apre il documento intero nell'editor
        // (un diff contro il vuoto non serve). Un file eliminato (D) o mancante (!) resta un diff: il contenuto BASE a sinistra, vuoto a destra.
        if (target.SingleFile is { Status: SvnStatusKind.Unversioned or SvnStatusKind.Added } added)
        {
            try
            {
                await _host.OpenFileAsync(added.FullPath);
                SetStatus(Loc.T("File aperto: {0}", added.RelativePath));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                SetStatus(Loc.T("Errore apertura file: {0}", ex.Message));
            }

            return;
        }

        if (target.SingleFile is not { } entry || !CanDiff(entry))
        {
            SetStatus(target.SingleFile is { Status: SvnStatusKind.Unversioned }
                ? Loc.T("Il file non è versionato: non c'è nessun diff.")
                : Loc.T("Seleziona un file versionato per vedere il diff."));
            return;
        }

        try
        {
            var name = Path.GetFileName(entry.FullPath);
            var dir = NewTempDirectory();
            var baseDir = Directory.CreateDirectory(Path.Combine(dir, "BASE")).FullName;
            var left = Path.Combine(baseDir, name);
            try
            {
                await _svnService.ExportBaseAsync(entry.FullPath, left, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Senza BASE (file aggiunto, non ancora committato): il lato sinistro e' vuoto.
                BackendLog.Info($"BASE non disponibile per {entry.RelativePath}: {ex.Message}");
                await File.WriteAllBytesAsync(left, [], ct);
            }

            var right = entry.FullPath;
            if (entry.Status is SvnStatusKind.Missing or SvnStatusKind.Deleted || !File.Exists(right))
            {
                right = Path.Combine(dir, "working", name);
                Directory.CreateDirectory(Path.GetDirectoryName(right)!);
                await File.WriteAllBytesAsync(right, [], ct);
            }

            await _host.OpenDiffAsync(left, right, $"{name} (BASE)", $"{name} (working copy)", $"{name} — BASE ⇄ working copy");
            SetStatus(Loc.T("Diff aperto: {0}", entry.RelativePath));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore diff: {0}", ex.Message));
        }
    }

    /// <summary>Diff combinato dei file modificati di una cartella: un unico file .diff (svn diff) aperto nell'editor.</summary>
    private async Task ShowFolderDiffAsync(string key, CancellationToken ct)
    {
        var target = ResolveKey(key);
        var files = target.Entries.Where(CanDiff).ToList();
        if (files.Count == 0)
        {
            SetStatus(Loc.T("Nessuna modifica da confrontare in questa cartella."));
            return;
        }

        // Cartella vera: elenco dei file con diff nativo nella finestra Log (come Solution Explorer); le sezioni restano patch unica.
        if (!target.WholeSection)
        {
            await _host.ShowFolderDiffAsync(target.Path);
            return;
        }

        try
        {
            SetStatus(Loc.T("Preparazione del diff di {0} file...", files.Count));
            var parts = new List<string>();
            foreach (var file in files.Take(MaxFolderDiffFiles))
            {
                var diff = await _svnService.GetFileDiffAsync(file.FullPath, ct);
                if (!string.IsNullOrWhiteSpace(diff))
                    parts.Add(diff.TrimEnd());
            }

            if (files.Count > MaxFolderDiffFiles)
                parts.Add(Loc.T("… altri {0} file omessi.", files.Count - MaxFolderDiffFiles));

            var text = parts.Count == 0 ? Loc.T("(Nessuna differenza testuale)") : string.Join("\n\n", parts);
            var folderName = target.WholeSection ? ChangesTreeBuilder.SectionTitle(target.Section) : Path.GetFileName(target.Path);
            var file2 = Path.Combine(NewTempDirectory(), SafeFileName($"Diff {folderName}") + ".diff");
            await File.WriteAllTextAsync(file2, text, new UTF8Encoding(false), ct);
            await _host.OpenFileAsync(file2);
            SetStatus(Loc.T("Diff cartella aperto: {0} file.", files.Count));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore diff: {0}", ex.Message));
        }
    }

    private string NewTempDirectory()
    {
        var dir = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length == 0 ? "diff" : name;
    }

    /// <summary>Elimina le cartelle temporanee dei diff piu' vecchie di un giorno (best effort, all'avvio).</summary>
    internal void CleanTemp()
    {
        try
        {
            if (!Directory.Exists(TempRoot))
                return;

            foreach (var dir in Directory.EnumerateDirectories(TempRoot))
            {
                if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-1))
                    Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // best effort
        }
    }

    // ── Watcher del file system ──────────────────────────────────────

    private void ConfigureWatcher(string? workingCopyPath)
    {
        if (!EnableWatcher)
            return;

        string normalized;
        try
        {
            normalized = string.IsNullOrWhiteSpace(workingCopyPath) ? string.Empty : Path.GetFullPath(workingCopyPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalized = string.Empty; // percorso digitato a meta' o non valido
        }

        // Il watcher ricorsivo parte solo su una working copy vera (esiste e ha una root .svn): un percorso digitato
        // a meta', o una cartella enorme qualsiasi, non deve sorvegliare un intero disco.
        if (normalized.Length > 0 && (!Directory.Exists(normalized) || SvnService.FindSvnRoot(normalized) is null))
            normalized = string.Empty;

        lock (_watcherLock)
        {
            if (string.Equals(_watchedRoot, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            _watcher?.Dispose();
            _watcher = null;
            _watchedRoot = normalized;

            if (normalized.Length == 0)
                return;

            var watcher = new FileSystemWatcher(normalized)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.CreationTime
                               | NotifyFilters.Size,
            };

            watcher.Changed += OnFsChanged;
            watcher.Created += OnFsChanged;
            watcher.Deleted += OnFsChanged;
            watcher.Renamed += OnFsChanged;
            // Overflow del buffer (molti file cambiati insieme): gli eventi persi si recuperano con un refresh completo.
            watcher.Error += (_, _) => _debouncer.Request();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
    }

    private void OnFsChanged(object sender, FileSystemEventArgs e)
    {
        // bin/ obj/ .vs/ .svn/ cambiano di continuo senza toccare lo stato SVN.
        if (WorkingCopyPathFilter.IsNoise(_watchedRoot, e.FullPath))
            return;

        _debouncer.Request();
    }

    // ── Utilita' ─────────────────────────────────────────────────────

    /// <summary>Imposta un messaggio d'azione: resta visibile finche' non lo sostituisce un altro (i refresh automatici non lo toccano).</summary>
    private void SetStatus(string message)
    {
        _statusIsAuto = false;
        lock (_stateLock)
        {
            _statusMessage = message;
            _statusSeq++;
        }

        NotifyChanged();
    }

    /// <summary>Imposta un messaggio "di riepilogo": lo sostituiscono liberamente i refresh successivi.</summary>
    private void SetStatusAuto(string message)
    {
        lock (_stateLock)
            _statusMessage = message;
        NotifyChanged();
    }

    private static string NormalizeStatusPath(string statusPath, string workingCopyPath)
    {
        if (string.IsNullOrWhiteSpace(statusPath) || statusPath == ".")
            return workingCopyPath;

        return Path.IsPathRooted(statusPath)
            ? statusPath
            : Path.GetFullPath(Path.Combine(workingCopyPath, statusPath));
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        lock (_watcherLock)
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        _lifetime.Dispose();
    }
}
