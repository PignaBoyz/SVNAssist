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
    private int _started;

    private List<ChangeEntry> _entries = [];
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
        WizardStore? wizard = null)
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
            stagedPaths: () => StagedEntries().Select(e => e.FullPath).ToList(),
            workingCopyPath: () => WorkingCopyPath,
            dialogs: dialogs);
        CommitMessage.Changed += NotifyChanged;
        _debouncer = new RefreshDebouncer(RefreshQuietPeriod, AutoRefreshAsync);
    }

    /// <summary>Refresh automatico (watcher): salta se non c'e' una working copy o se un'operazione dell'utente e' in corso.</summary>
    internal Task AutoRefreshAsync(CancellationToken ct)
        => string.IsNullOrWhiteSpace(WorkingCopyPath) || OperationRunning ? Task.CompletedTask : RefreshAsync(ct, reloadBranch: false);

    /// <summary>Messaggio di commit e azioni AI.</summary>
    internal CommitStore CommitMessage { get; }

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
        set { lock (_stateLock) _workingCopyPath = value; }
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
            return new ChangesState(
                new WorkingCopyDto(_workingCopyPath, _showWorkingCopyInput, _branchName),
                CommitMessage.GetSnapshot(download),
                _isTreeView ? "tree" : "list",
                new ThemeDto(mode, mode == "auto" ? _vsThemeKind : mode),
                sections,
                BuildStatus(),
                Prerequisites.GetSnapshot(),
                _loading,
                Wizard.GetSnapshot());
        }
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
                    Diff: CanDiff(entry),
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

    private static bool CanDeleteFile(ChangeEntry e) => !e.IsDirectory && e.Status != SvnStatusKind.Deleted;

    private static CanDto FolderCan(ChangeSection section, List<ChangeEntry> entries) => new(
        Diff: entries.Any(CanDiff),
        Stage: section != ChangeSection.Staged && entries.Any(e => e.Status is not (SvnStatusKind.Conflicted or SvnStatusKind.Normal)),
        Unstage: section == ChangeSection.Staged,
        Add: entries.Any(e => e.Status == SvnStatusKind.Unversioned),
        Ignore: entries.Any(e => e.Status == SvnStatusKind.Unversioned),
        Revert: entries.Any(e => StatusPresentation.IsRevertable(e.Status)),
        Resolve: false,
        DeleteFile: false);

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
                await RunExclusiveAsync(() => UpdateToRevisionCoreAsync(ct));
                break;
            case "cleanup":
                await RunExclusiveAsync(() => CleanupCoreAsync(ct));
                break;
            case "commit":
                await RunExclusiveAsync(() => CommitCoreAsync(ct));
                break;
            case "setCommitText":
                CommitMessage.SetTextQuietly(Args.Str(args, "text"));
                break;
            case "genAi":
                await CommitMessage.GenerateAsync(ct);
                break;
            case "improveAi":
                await CommitMessage.ImproveAsync(ct);
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
            var branchTask = needBranch ? _svnService.GetBranchNameAsync(path, ct) : null;

            var statuses = await statusTask;
            string? branch = null;
            if (branchTask is not null)
                branch = await branchTask;

            var newEntries = statuses
                .Where(s => s.Status != SvnStatusKind.Normal || s.IsLocked)
                .Select(s => ToEntry(s, path))
                .Where(e => e is not null)
                .Select(e => e!)
                .ToList();

            lock (_stateLock)
            {
                if (branch is not null)
                {
                    _branchName = branch;
                    _branchLoadedFor = path;
                }

                // La working copy puo' essere cambiata mentre svn girava: il risultato vecchio si scarta.
                if (!string.Equals(_workingCopyPath, path, StringComparison.OrdinalIgnoreCase))
                    return;

                _entries = newEntries;
                // Una working copy digitata a mano resta modificabile; una rilevata dalla solution nasconde il campo.
                _showWorkingCopyInput = !_autoDetected;
                UpdateStagedAfterRefresh();
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

    /// <summary>I nuovi elementi M/A/D/R/! partono in Staged; gli altri mantengono la scelta dell'utente.</summary>
    private void UpdateStagedAfterRefresh()
    {
        var state = Wc;
        var current = new HashSet<string>(_entries.Select(e => e.FullPath), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries.Where(e => !state.Known.Contains(e.FullPath)))
        {
            // Missing (!) non entra da solo: un salvataggio transitorio di VS diventerebbe una cancellazione nel commit.
            if (entry.Status is SvnStatusKind.Modified or SvnStatusKind.Added or SvnStatusKind.Deleted
                or SvnStatusKind.Replaced)
                state.Staged.Add(entry.FullPath);
        }

        state.Staged.RemoveWhere(p => !current.Contains(p));
        state.Known = current;
    }

    private string BuildSummary()
    {
        List<ChangeEntry> entries;
        lock (_stateLock)
            entries = _entries;

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

    private List<ChangeEntry> StagedEntries()
    {
        lock (_stateLock)
            return _entries.Where(e => SectionOf(e) == ChangeSection.Staged).ToList();
    }

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
    private async Task RunExclusiveAsync(Func<Task> body)
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
    /// Commit dei soli elementi in Staged. Come TortoiseSVN: i non versionati in Staged vengono prima
    /// aggiunti e i mancanti prima cancellati (svn commit da solo li rifiuterebbe).
    /// </summary>
    private async Task CommitCoreAsync(CancellationToken ct)
    {
        List<ChangeEntry> selected;
        List<ChangeEntry> all;
        lock (_stateLock)
        {
            all = _entries;
            var staged = Wc.Staged;
            selected = _entries.Where(e => staged.Contains(e.FullPath)).ToList();
        }

        if (selected.Count == 0)
        {
            SetStatus(Loc.T("Metti almeno un file in Staged per il commit."));
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
            SetStatus(Loc.T("Risolvi prima i conflitti ({0} file) o toglili da Staged: svn non committa file in conflitto.", conflicted.Count));
            return;
        }

        try
        {
            SetStatus(Loc.T("Commit in corso..."));

            // Come TortoiseSVN: le cartelle padre nuove (aggiunte o non versionate) di un elemento in Staged vanno
            // nel commit anche se non ci sono, altrimenti svn rifiuta ("is not known to exist in the repository
            // and is not part of the commit, yet its child is part of the commit").
            var parents = NewParentFolders(selected, all);
            var commitEntries = parents.Concat(selected).ToList();

            var toAdd = commitEntries.Where(e => e.Status == SvnStatusKind.Unversioned).Select(e => e.FullPath).ToList();
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
            var revision = await _svnService.CommitAsync(paths, CommitMessage.Text.Trim(), ct, wholeFolders);

            CommitMessage.Clear();

            await RefreshAsync(ct, reloadBranch: true, revision is { } rev
                ? Loc.T("Commit completato: revisione {0} ({1} elementi).", rev, selected.Count)
                : Loc.T("Commit completato: {0} elementi.", selected.Count));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Add/delete gia' eseguiti prima del commit fallito (es. "fai prima Update"): la lista va riallineata.
            await RefreshAsync(ct, reloadBranch: false, Loc.T("Errore commit: {0}", ex.Message));
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
            var update = await _svnService.UpdateAsync(wc, ct);
            var updated = update.Revision is { } rev ? Loc.T("Aggiornato alla revisione {0}", rev) : Loc.T("Update completato");
            await RefreshAsync(ct, reloadBranch: true, update.ConflictedPaths.Count > 0
                ? Loc.T("{0}: {1} file in conflitto.", updated, update.ConflictedPaths.Count)
                : $"{updated}.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore update: {0}", ex.Message));
        }
    }

    /// <summary>SVN Update to revision: chiede la revisione (intero positivo o HEAD) e fa <c>svn update -r</c> sulla working copy.</summary>
    private async Task UpdateToRevisionCoreAsync(CancellationToken ct)
    {
        var wc = WorkingCopyPath;
        if (string.IsNullOrWhiteSpace(wc))
        {
            SetStatus(Loc.T("Nessuna working copy configurata."));
            return;
        }

        var revision = await AskRevisionAsync(ct);
        if (revision is null)
            return;

        try
        {
            SetStatus(Loc.T("Update alla revisione {0} in corso...", revision));
            var update = await _svnService.UpdateToRevisionAsync(wc, revision, ct);
            var updated = update.Revision is { } rev ? Loc.T("Aggiornato alla revisione {0}", rev) : Loc.T("Update completato");
            await RefreshAsync(ct, reloadBranch: true, update.ConflictedPaths.Count > 0
                ? Loc.T("{0}: {1} file in conflitto.", updated, update.ConflictedPaths.Count)
                : $"{updated}.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore update: {0}", ex.Message));
        }
    }

    /// <summary>Chiede la revisione finché non è valida; <c>null</c> se l'utente annulla.</summary>
    private async Task<string?> AskRevisionAsync(CancellationToken ct)
    {
        var message = Loc.T("Aggiorna la working copy alla revisione indicata (numero intero positivo oppure HEAD).");
        while (true)
        {
            var typed = await _dialogs.AskTextAsync(message, Loc.T("Revisione"), "HEAD", ct);
            if (typed is null)
                return null;
            if (SvnRevision.TryNormalize(typed, out var revision))
                return revision;
            message = Loc.T("\"{0}\" non è una revisione valida: scrivi un numero intero positivo (es. 1234) oppure HEAD.", typed.Trim());
        }
    }

    /// <summary>SVN Cleanup della working copy (rimuove i lock lasciati da operazioni interrotte).</summary>
    private async Task CleanupCoreAsync(CancellationToken ct)
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
            await _svnService.CleanupAsync(wc, ct);
            await RefreshAsync(ct, reloadBranch: false, Loc.T("Cleanup completato."));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore cleanup: {0}", ex.Message));
        }
    }

    /// <summary>Messaggio di stato + eventuale pulsante d'azione: working copy bloccata -> "Esegui Cleanup".</summary>
    private StatusDto BuildStatus()
    {
        // "Errore..." (it) e "Error..." (en) iniziano uguale: il test non dipende dalla lingua.
        var isError = _statusMessage.StartsWith("Error", StringComparison.Ordinal);
        return new StatusDto(_statusMessage, isError,
            isError && SvnErrors.IsWorkingCopyLocked(_statusMessage) ? new StatusActionDto(Loc.T("Esegui Cleanup"), "cleanup") : null);
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
    /// Tasto Canc su un file: lo elimina dal file system (non versionato: nel cestino; versionato: <c>svn delete --force</c>),
    /// dopo la conferma e con avviso se ha modifiche locali. Non si applica alle cartelle.
    /// </summary>
    private async Task DeleteFileCoreAsync(KeyTarget target, CancellationToken ct)
    {
        var entry = target.SingleFile;
        if (entry is null || !CanDeleteFile(entry))
        {
            SetStatus(target.Entries.Any(e => e.IsDirectory) || target.Entries.Count != 1
                ? Loc.T("L'eliminazione con Canc non è disponibile sulle cartelle.")
                : Loc.T("Nessun file idoneo per l'eliminazione."));
            return;
        }

        // Difesa in profondita': il file viene da svn status, ma non si tocca mai nulla fuori dalla working copy.
        if (!IsUnderWorkingCopy(entry.FullPath))
        {
            SetStatus(Loc.T("Errore: il file non si trova nella working copy."));
            return;
        }

        var versioned = entry.Status != SvnStatusKind.Unversioned;
        var message = versioned
            ? Loc.T("Eliminare \"{0}\"?\n\nIl file verrà eliminato dalla cartella di lavoro e rimosso da SVN al prossimo commit.", entry.RelativePath)
            : Loc.T("Eliminare \"{0}\" dal disco?\n\nIl file non è versionato: verrà spostato nel cestino.", entry.RelativePath);
        var hasLocalChanges = entry.Status is SvnStatusKind.Modified or SvnStatusKind.Replaced or SvnStatusKind.Conflicted or SvnStatusKind.Added;
        if (hasLocalChanges)
            message += "\n\n" + Loc.T("ATTENZIONE: il file ha modifiche locali non committate, che andranno perse.");

        if (!await _dialogs.ConfirmAsync(message, ct))
            return;

        try
        {
            SetStatus(Loc.T("Eliminazione in corso..."));
            if (versioned)
                await _svnService.DeleteAsync([entry.FullPath], ct, force: true);
            else
                DeleteFromDisk(entry.FullPath);

            await RefreshAsync(ct, reloadBranch: false, Loc.T("Eliminato: {0}.", entry.RelativePath));
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
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && File.Exists(path))
        {
            // Cestino non disponibile (es. unita' di rete): eliminazione definitiva, l'utente ha gia' confermato.
            BackendLog.Warn("Cestino non disponibile, eliminazione diretta: " + ex.Message);
            File.Delete(path);
        }
    }

    /// <summary>SVN Revert su file, cartella o sezione. Chiede conferma elencando cosa verra' annullato.</summary>
    private async Task RevertCoreAsync(KeyTarget target, CancellationToken ct)
    {
        // Percorsi espliciti: si annulla esattamente cio' che la conferma elenca (vedi RevertPlan).
        var targets = RevertPlan(target.Entries.Where(e => StatusPresentation.IsRevertable(e.Status)).ToList());
        if (targets.Count == 0)
        {
            SetStatus(Loc.T("Nessun elemento idoneo per SVN Revert."));
            return;
        }

        var scope = target.WholeSection
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
            if (!await _dialogs.ConfirmAsync(
                    Loc.T("Conflitto ad albero su \"{0}\" (rinomina/cancellazione). " +
                          "Sistema prima la struttura a mano, poi conferma per segnarlo come risolto.", entry.RelativePath), ct))
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
            _statusMessage = message;
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
