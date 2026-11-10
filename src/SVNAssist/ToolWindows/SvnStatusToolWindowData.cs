using System.IO;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;
using SVNAssist.Models;
using SVNAssist.Services;

namespace SVNAssist.ToolWindows;

/// <summary>
/// Data context (ViewModel) del pannello "SVN Changes", sul modello di Git Changes di Visual Studio:
/// branch + Update, messaggio di commit con AI, sezioni Conflitti / Modifiche / Non versionati
/// in vista Albero o Lista, checkbox tri-state su cartelle e sezioni, checklist dei prerequisiti.
/// </summary>
/// <remarks>
/// Nel modello Remote UI il data context è <c>[DataContract]</c>, le proprietà bindabili hanno
/// <c>[DataMember]</c> e i comandi sono <see cref="IAsyncCommand"/>. L'albero è un'unica lista
/// appiattita di <see cref="ChangeRowViewModel"/>. I pezzi indipendenti vivono in classi a parte:
/// <see cref="ChangesTreeBuilder"/> / <see cref="ChangeRowFactory"/> (raggruppamento, puro),
/// <see cref="CommitMessageViewModel"/> (messaggio + AI), <see cref="PrerequisitesViewModel"/>.
/// Gli handler sono <c>internal</c> per poterli chiamare direttamente dai test.
/// </remarks>
[DataContract]
internal class SvnStatusToolWindowData : NotifyPropertyChangedObject
{
    /// <summary>Oltre questo numero il diff di una cartella viene troncato (ogni file è una chiamata a svn).</summary>
    internal const int MaxFolderDiffFiles = 100;
    private const int MaxListedInConfirmation = 8;

    private readonly ISvnService _svnService;
    private readonly ISvnAssistSettings _settingsService;
    private readonly IDialogService _dialogs;
    private readonly SemaphoreSlim _autoDetectLock = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    // Modello: elementi di svn status, selezione per il commit e stato espanso/compresso.
    private List<ChangeEntry> _entries = [];
    private readonly HashSet<string> _selection = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    private string _workingCopyPath = string.Empty;
    private string? _branchLoadedFor;
    private bool _autoDetected;
    private bool _statusIsAuto = true;
    private string _statusMessage = "Caricamento in corso...";
    private bool _showWorkingCopyInput;
    private bool _isTreeView = true;
    private bool _isEmpty = true;
    private string _branchName = string.Empty;
    private string _diffContent = string.Empty;
    private string _diffTitle = string.Empty;
    private bool _isDiffVisible;

    public SvnStatusToolWindowData(
        ISvnAssistSettings settingsService,
        ISvnService svnService,
        IAiServiceFactory aiServiceFactory,
        IAiSetupPrompt aiSetupPrompt,
        IDialogService dialogs,
        PrerequisitesViewModel prerequisites)
    {
        _settingsService = settingsService;
        _svnService = svnService;
        _dialogs = dialogs;
        Prerequisites = prerequisites;
        Prerequisites.SvnBecameAvailable = ct => RefreshAsync(ct, reloadBranch: true);
        CommitMessage = new CommitMessageViewModel(
            settingsService, svnService, aiServiceFactory, aiSetupPrompt,
            selectedPaths: () => SelectedEntries().Select(e => e.FullPath).ToList(),
            workingCopyPath: () => WorkingCopyPath);

        Rows = [];
        RefreshCommand = new AsyncCommand(OnRefreshAsync);
        CommitCommand = new AsyncCommand(CommitAsync);
        UpdateCommand = new AsyncCommand(UpdateAsync);
        ShowFileDiffCommand = new AsyncCommand(ShowFileDiffAsync);
        ShowFolderDiffCommand = new AsyncCommand(ShowFolderDiffAsync);
        CloseDiffCommand = new AsyncCommand(CloseDiffAsync);
        SelectAllCommand = new AsyncCommand((_, _) => SetAllSelectedAsync(true));
        UnselectAllCommand = new AsyncCommand((_, _) => SetAllSelectedAsync(false));
        ToggleExpandCommand = new AsyncCommand(ToggleExpandAsync);
        ExpandAllCommand = new AsyncCommand((_, _) => SetAllExpandedAsync(true));
        CollapseAllCommand = new AsyncCommand((_, _) => SetAllExpandedAsync(false));
        ShowTreeViewCommand = new AsyncCommand((_, _) => SetTreeViewAsync(true));
        ShowListViewCommand = new AsyncCommand((_, _) => SetTreeViewAsync(false));
        AddToSvnCommand = new AsyncCommand(AddAsync);
        DeleteFromSvnCommand = new AsyncCommand(DeleteAsync);
        RevertCommand = new AsyncCommand(RevertAsync);
        ResolveConflictCommand = new AsyncCommand(ResolveAsync);
        AddToIgnoreListCommand = new AsyncCommand(IgnoreAsync);
    }

    /// <summary>Verifica se un percorso è una cartella (iniettabile nei test: di default il file system).</summary>
    internal Func<string, bool> IsDirectory { get; set; } = Directory.Exists;

    // ── Oggetti annidati ─────────────────────────────────────────────

    /// <summary>Messaggio di commit e azioni AI.</summary>
    [DataMember]
    public CommitMessageViewModel CommitMessage { get; }

    /// <summary>Checklist dei prerequisiti.</summary>
    [DataMember]
    public PrerequisitesViewModel Prerequisites { get; }

    // ── Proprietà ────────────────────────────────────────────────────

    /// <summary>Nome del branch SVN corrente (es. "trunk", "branches/feature-x").</summary>
    [DataMember]
    public string BranchName
    {
        get => _branchName;
        set => SetProperty(ref _branchName, value);
    }

    /// <summary>Righe appiattite: sezioni, cartelle e file.</summary>
    [DataMember]
    public ObservableList<ChangeRowViewModel> Rows { get; }

    /// <summary>Percorso della working copy SVN da analizzare.</summary>
    [DataMember]
    public string WorkingCopyPath
    {
        get => _workingCopyPath;
        set => SetProperty(ref _workingCopyPath, value);
    }

    /// <summary>Messaggio di stato mostrato in basso.</summary>
    [DataMember]
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Mostra il campo per il percorso manuale della working copy (nascosto se auto-rilevata).</summary>
    [DataMember]
    public bool ShowWorkingCopyInput
    {
        get => _showWorkingCopyInput;
        set => SetProperty(ref _showWorkingCopyInput, value);
    }

    /// <summary><c>true</c> per la vista Albero, <c>false</c> per la vista Lista.</summary>
    [DataMember]
    public bool IsTreeView
    {
        get => _isTreeView;
        set => SetProperty(ref _isTreeView, value);
    }

    /// <summary>Non c'è nessuna modifica da mostrare.</summary>
    [DataMember]
    public bool IsEmpty
    {
        get => _isEmpty;
        set => SetProperty(ref _isEmpty, value);
    }

    /// <summary>Contenuto del diff mostrato nel pannello inferiore.</summary>
    [DataMember]
    public string DiffContent
    {
        get => _diffContent;
        set => SetProperty(ref _diffContent, value);
    }

    /// <summary>Titolo del pannello diff.</summary>
    [DataMember]
    public string DiffTitle
    {
        get => _diffTitle;
        set => SetProperty(ref _diffTitle, value);
    }

    /// <summary>Il pannello diff è visibile.</summary>
    [DataMember]
    public bool IsDiffVisible
    {
        get => _isDiffVisible;
        set => SetProperty(ref _isDiffVisible, value);
    }

    // ── Comandi ──────────────────────────────────────────────────────

    /// <summary>Ricarica modifiche e branch.</summary>
    [DataMember]
    public IAsyncCommand RefreshCommand { get; }

    /// <summary>Commit dei file selezionati.</summary>
    [DataMember]
    public IAsyncCommand CommitCommand { get; }

    /// <summary>Esegue <c>svn update</c>.</summary>
    [DataMember]
    public IAsyncCommand UpdateCommand { get; }

    /// <summary>Diff di un file (parametro: la riga).</summary>
    [DataMember]
    public IAsyncCommand ShowFileDiffCommand { get; }

    /// <summary>Diff combinato di una cartella (parametro: la riga).</summary>
    [DataMember]
    public IAsyncCommand ShowFolderDiffCommand { get; }

    /// <summary>Chiude il pannello diff.</summary>
    [DataMember]
    public IAsyncCommand CloseDiffCommand { get; }

    /// <summary>Seleziona tutto.</summary>
    [DataMember]
    public IAsyncCommand SelectAllCommand { get; }

    /// <summary>Deseleziona tutto.</summary>
    [DataMember]
    public IAsyncCommand UnselectAllCommand { get; }

    /// <summary>Espande/comprime una sezione o cartella (parametro: la riga).</summary>
    [DataMember]
    public IAsyncCommand ToggleExpandCommand { get; }

    /// <summary>Espande tutte le cartelle.</summary>
    [DataMember]
    public IAsyncCommand ExpandAllCommand { get; }

    /// <summary>Comprime tutte le cartelle.</summary>
    [DataMember]
    public IAsyncCommand CollapseAllCommand { get; }

    /// <summary>Passa alla vista Albero.</summary>
    [DataMember]
    public IAsyncCommand ShowTreeViewCommand { get; }

    /// <summary>Passa alla vista Lista.</summary>
    [DataMember]
    public IAsyncCommand ShowListViewCommand { get; }

    /// <summary>SVN Add su file, cartella o sezione (parametro: la riga).</summary>
    [DataMember]
    public IAsyncCommand AddToSvnCommand { get; }

    /// <summary>SVN Delete su un file (parametro: la riga).</summary>
    [DataMember]
    public IAsyncCommand DeleteFromSvnCommand { get; }

    /// <summary>SVN Revert su file, cartella o sezione (parametro: la riga), con conferma.</summary>
    [DataMember]
    public IAsyncCommand RevertCommand { get; }

    /// <summary>Risolve il conflitto di un file (parametro: la riga).</summary>
    [DataMember]
    public IAsyncCommand ResolveConflictCommand { get; }

    /// <summary>Aggiunge alla ignore list (parametro: la riga).</summary>
    [DataMember]
    public IAsyncCommand AddToIgnoreListCommand { get; }

    // ── Avvio e auto-detect ──────────────────────────────────────────

    /// <summary>Controlla i prerequisiti (apre la checklist se manca svn) e carica la lingua dell'AI.</summary>
    internal async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            await Prerequisites.InitializeAsync(ct);
            await CommitMessage.InitializeAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Prerequisites.Feedback = $"Controllo prerequisiti non riuscito: {ex.Message}";
        }
    }

    /// <summary>Refresh programmatico (usato dal watcher e all'apertura). Il branch si rilegge solo se serve.</summary>
    internal Task RefreshAsync(CancellationToken ct) => RefreshAsync(ct, reloadBranch: false);

    /// <summary>Applica il risultato dell'auto-detect della working copy dalla solution aperta.</summary>
    internal async Task ApplyAutoDetectedWorkingCopyAsync(string? svnRoot, CancellationToken ct)
    {
        await _autoDetectLock.WaitAsync(ct);
        try
        {
            if (string.IsNullOrWhiteSpace(svnRoot))
            {
                // Un percorso scritto a mano non va azzerato dal polling della solution.
                if (_autoDetected)
                {
                    _autoDetected = false;
                    WorkingCopyPath = string.Empty;
                    BranchName = string.Empty;
                    _branchLoadedFor = null;
                    _entries = [];
                    ApplyEntries();
                    ShowWorkingCopyInput = true;
                    CloseDiff();
                }

                if (string.IsNullOrWhiteSpace(WorkingCopyPath))
                {
                    ShowWorkingCopyInput = true;
                    StatusMessage = "La solution corrente non è una working copy SVN.";
                }

                return;
            }

            // Stessa working copy: niente refresh (il watcher si occupa dei cambiamenti).
            if (string.Equals(WorkingCopyPath, svnRoot, StringComparison.OrdinalIgnoreCase))
                return;

            _autoDetected = true;
            WorkingCopyPath = svnRoot;
            ShowWorkingCopyInput = false;
            StatusMessage = "Working copy SVN rilevata automaticamente, aggiornamento...";
            await RefreshAsync(ct, reloadBranch: true);
        }
        finally
        {
            _autoDetectLock.Release();
        }
    }

    /// <summary>Nessuna solution aperta / query fallita: avvisa solo se non c'è già una working copy.</summary>
    internal void NotifyNoSolution()
    {
        if (string.IsNullOrWhiteSpace(WorkingCopyPath))
            StatusMessage = "Inserisci il percorso della working copy e premi Refresh.";
    }

    // ── Refresh ──────────────────────────────────────────────────────

    private Task OnRefreshAsync(object? parameter, CancellationToken ct)
    {
        // Refresh esplicito: rilegge anche il branch e torna a mostrare il riepilogo.
        _statusIsAuto = true;
        return RefreshAsync(ct, reloadBranch: true);
    }

    /// <summary>
    /// Legge <c>svn status</c> e ricostruisce le righe. <c>svn info</c> (branch) solo se la working copy
    /// è cambiata o se <paramref name="reloadBranch"/> (update, commit, refresh esplicito).
    /// </summary>
    internal async Task RefreshAsync(CancellationToken ct, bool reloadBranch, string? statusMessage = null)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            if (string.IsNullOrWhiteSpace(WorkingCopyPath))
            {
                var settingsPath = await _settingsService.GetWorkingCopyPathAsync(ct);
                if (!string.IsNullOrWhiteSpace(settingsPath))
                    WorkingCopyPath = settingsPath;
            }

            if (string.IsNullOrWhiteSpace(WorkingCopyPath))
            {
                ShowWorkingCopyInput = true;
                StatusMessage = "Inserisci il percorso della working copy.";
                return;
            }

            ShowWorkingCopyInput = false;
            var path = WorkingCopyPath;
            var needBranch = reloadBranch || !string.Equals(_branchLoadedFor, path, StringComparison.OrdinalIgnoreCase);

            var statusTask = _svnService.GetStatusAsync(path, ct);
            var branchTask = needBranch ? _svnService.GetBranchNameAsync(path, ct) : null;

            var statuses = await statusTask;
            if (branchTask is not null)
            {
                BranchName = await branchTask;
                _branchLoadedFor = path;
            }

            _entries = statuses
                .Where(s => s.Status != SvnStatusKind.Normal)
                .Select(s => ToEntry(s, path))
                .ToList();
            UpdateSelectionAfterRefresh();
            ApplyEntries();

            if (statusMessage is not null)
                SetStatus(statusMessage);
            else if (_statusIsAuto)
                StatusMessage = BuildSummary();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus($"Errore: {ex.Message}");
            ShowWorkingCopyInput = true;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private ChangeEntry ToEntry(SvnFileStatus status, string workingCopy)
    {
        var fullPath = NormalizeStatusPath(status.Path, workingCopy);
        var relative = string.Equals(fullPath, workingCopy, StringComparison.OrdinalIgnoreCase)
            ? "."
            : Path.GetRelativePath(workingCopy, fullPath);
        return new ChangeEntry(fullPath, relative, status.Status, status.IsTreeConflict, IsDirectory(fullPath));
    }

    /// <summary>I nuovi elementi hanno la selezione di default (come TortoiseSVN); gli altri mantengono la scelta dell'utente.</summary>
    private void UpdateSelectionAfterRefresh()
    {
        var current = new HashSet<string>(_entries.Select(e => e.FullPath), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries.Where(e => !_known.Contains(e.FullPath)))
        {
            if (entry.Status is SvnStatusKind.Modified or SvnStatusKind.Added or SvnStatusKind.Deleted
                or SvnStatusKind.Replaced or SvnStatusKind.Missing)
                _selection.Add(entry.FullPath);
        }

        _selection.RemoveWhere(p => !current.Contains(p));
        _known = current;
    }

    private string BuildSummary()
    {
        if (_entries.Count == 0)
            return "Nessuna modifica.";

        var parts = new List<string>();
        var conflicts = _entries.Count(e => e.Section == ChangeSection.Conflicts);
        var changes = _entries.Count(e => e.Section == ChangeSection.Changes);
        var unversioned = _entries.Count(e => e.Section == ChangeSection.Unversioned);
        if (conflicts > 0)
            parts.Add($"{conflicts} in conflitto");
        if (changes > 0)
            parts.Add($"{changes} modificati");
        if (unversioned > 0)
            parts.Add($"{unversioned} non versionati");
        return string.Join(", ", parts) + ".";
    }

    /// <summary>Ricostruisce le righe: se la struttura è invariata aggiorna quelle esistenti (la ListBox non scatta in cima).</summary>
    private void ApplyEntries()
    {
        var fresh = ChangeRowFactory.Build(_entries, IsTreeView, _collapsed, WorkingCopyPath);
        foreach (var row in fresh)
            row.ToggleRequested = ToggleRowChecked;

        var sameShape = fresh.Count == Rows.Count && fresh.Select(r => r.Key).SequenceEqual(Rows.Select(r => r.Key));
        if (sameShape)
        {
            for (var i = 0; i < fresh.Count; i++)
                Rows[i].CopyPresentationFrom(fresh[i]);
        }
        else
        {
            Rows.Clear();
            Rows.AddRange(fresh);
        }

        IsEmpty = Rows.Count == 0;
        UpdateCheckStates();
    }

    // ── Selezione (checkbox tri-state) ───────────────────────────────

    private bool IsSelected(ChangeEntry entry) => _selection.Contains(entry.FullPath);

    private IEnumerable<ChangeEntry> SelectedEntries() => _entries.Where(IsSelected);

    private void UpdateCheckStates(ChangeRowViewModel? forced = null)
    {
        foreach (var row in Rows)
            row.ApplyChecked(ChangesTreeBuilder.GetCheckState(row.Entries, IsSelected), force: ReferenceEquals(row, forced));
    }

    /// <summary>
    /// Click sulla checkbox di una riga: se non tutto è selezionato seleziona tutto, altrimenti deseleziona.
    /// Il valore proposto dal ciclo tri-state di WPF viene ignorato, così partendo da "parziale" si seleziona tutto.
    /// </summary>
    internal void ToggleRowChecked(ChangeRowViewModel row)
    {
        var select = ChangesTreeBuilder.GetCheckState(row.Entries, IsSelected) != true;
        foreach (var entry in row.Entries)
        {
            if (select)
                _selection.Add(entry.FullPath);
            else
                _selection.Remove(entry.FullPath);
        }

        UpdateCheckStates(forced: row);
    }

    internal Task SetAllSelectedAsync(bool selected)
    {
        if (selected)
            _selection.UnionWith(_entries.Select(e => e.FullPath));
        else
            _selection.Clear();

        UpdateCheckStates();
        return Task.CompletedTask;
    }

    // ── Albero: espandi / comprimi / vista ───────────────────────────

    internal Task ToggleExpandAsync(object? parameter, CancellationToken ct)
    {
        if (parameter is ChangeRowViewModel { IsFile: false } row && !_collapsed.Remove(row.Key))
            _collapsed.Add(row.Key);

        ApplyEntries();
        return Task.CompletedTask;
    }

    internal Task SetAllExpandedAsync(bool expanded)
    {
        _collapsed.Clear();
        if (!expanded)
        {
            foreach (var (section, entries) in ChangeRowFactory.Sections(_entries))
                foreach (var key in ChangesTreeBuilder.FolderKeys(ChangesTreeBuilder.BuildTree(section, entries)))
                    _collapsed.Add(key);
        }

        ApplyEntries();
        return Task.CompletedTask;
    }

    internal Task SetTreeViewAsync(bool tree)
    {
        IsTreeView = tree;
        ApplyEntries();
        return Task.CompletedTask;
    }

    // ── Commit e update ──────────────────────────────────────────────

    /// <summary>
    /// Commit dei soli elementi selezionati. Come TortoiseSVN: i non versionati spuntati vengono prima
    /// aggiunti e i mancanti spuntati prima cancellati (svn commit da solo li rifiuterebbe).
    /// </summary>
    internal async Task CommitAsync(object? parameter, CancellationToken ct)
    {
        var selected = SelectedEntries().ToList();
        if (selected.Count == 0)
        {
            SetStatus("Seleziona almeno un file per il commit.");
            return;
        }

        if (string.IsNullOrWhiteSpace(CommitMessage.Text))
        {
            SetStatus("Scrivi un messaggio di commit.");
            return;
        }

        var conflicted = selected.Where(e => e.Status == SvnStatusKind.Conflicted).ToList();
        if (conflicted.Count > 0)
        {
            SetStatus($"Risolvi prima i conflitti ({conflicted.Count} file) o deselezionali: svn non committa file in conflitto.");
            return;
        }

        try
        {
            SetStatus("Commit in corso...");

            // Come TortoiseSVN: le cartelle padre nuove (aggiunte o non versionate) di un elemento spuntato vanno
            // nel commit anche se non spuntate, altrimenti svn rifiuta ("is not known to exist in the repository
            // and is not part of the commit, yet its child is part of the commit").
            var parents = NewParentFolders(selected);
            var commitEntries = parents.Concat(selected).ToList();

            var toAdd = commitEntries.Where(e => e.Status == SvnStatusKind.Unversioned).Select(e => e.FullPath).ToList();
            if (toAdd.Count > 0)
                await _svnService.AddAsync(toAdd, ct);

            var toDelete = commitEntries.Where(e => e.Status == SvnStatusKind.Missing).Select(e => e.FullPath).ToList();
            if (toDelete.Count > 0)
                await _svnService.DeleteAsync(toDelete, ct);

            // Una cartella non versionata spuntata è una riga sola ma contiene un albero: "svn add" lo aggiunge
            // tutto, quindi nel commit entra per intero (le sue sottovoci non sono elencate singolarmente).
            var wholeFolders = commitEntries
                .Where(e => e.Status == SvnStatusKind.Unversioned && e.IsDirectory).Select(e => e.FullPath).ToList();

            var paths = commitEntries.Select(e => e.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var revision = await _svnService.CommitAsync(paths, CommitMessage.Text.Trim(), ct, wholeFolders);

            CommitMessage.Text = string.Empty;
            CommitMessage.AiStatus = string.Empty;

            await RefreshAsync(ct, reloadBranch: true, revision is { } rev
                ? $"Commit completato: revisione {rev} ({selected.Count} elementi)."
                : $"Commit completato: {selected.Count} elementi.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Add/delete già eseguiti prima del commit fallito (es. "fai prima Update"): la lista va riallineata.
            await RefreshAsync(ct, reloadBranch: false, $"Errore commit: {ex.Message}");
        }
    }

    /// <summary>
    /// Cartelle aggiunte (o non versionate) che contengono un elemento spuntato ma non sono spuntate esse stesse,
    /// dalla più esterna alla più interna.
    /// </summary>
    private List<ChangeEntry> NewParentFolders(IReadOnlyList<ChangeEntry> selected)
    {
        var chosen = new HashSet<string>(selected.Select(e => e.FullPath), StringComparer.OrdinalIgnoreCase);
        var byPath = _entries.ToDictionary(e => e.FullPath, StringComparer.OrdinalIgnoreCase);
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

    internal async Task UpdateAsync(object? parameter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(WorkingCopyPath))
        {
            SetStatus("Nessuna working copy configurata.");
            return;
        }

        try
        {
            SetStatus("Update in corso...");
            var update = await _svnService.UpdateAsync(WorkingCopyPath, ct);
            var updated = update.Revision is { } rev ? $"Aggiornato alla revisione {rev}" : "Update completato";
            await RefreshAsync(ct, reloadBranch: true, update.ConflictedPaths.Count > 0
                ? $"{updated}: {update.ConflictedPaths.Count} file in conflitto."
                : $"{updated}.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus($"Errore update: {ex.Message}");
        }
    }

    // ── Operazioni su file, cartelle e sezioni ───────────────────────

    /// <summary>SVN Add. Su una cartella non versionata passa il percorso della cartella (svn add --force è ricorsivo).</summary>
    internal async Task AddAsync(object? parameter, CancellationToken ct)
    {
        if (parameter is not ChangeRowViewModel row)
            return;

        var affected = row.Entries.Where(e => e.Status == SvnStatusKind.Unversioned).ToList();
        var paths = row.Kind == ChangeRowKind.Folder && row.Section == ChangeSection.Unversioned
            ? [row.FolderFullPath]
            : affected.Select(e => e.FullPath).ToList();
        if (affected.Count == 0)
        {
            SetStatus("Nessun elemento non versionato da aggiungere.");
            return;
        }

        try
        {
            SetStatus("Aggiunta a SVN in corso...");
            await _svnService.AddAsync(paths, ct);

            // Chi aggiunge di solito vuole anche committare: gli elementi aggiunti restano spuntati.
            foreach (var entry in affected)
                _selection.Add(entry.FullPath);

            await RefreshAsync(ct, reloadBranch: false, $"Aggiunti a SVN: {affected.Count} elementi.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus($"Errore add: {ex.Message}");
        }
    }

    /// <summary>SVN Delete di un file, con conferma (e avviso se ha modifiche locali).</summary>
    internal async Task DeleteAsync(object? parameter, CancellationToken ct)
    {
        if (parameter is not ChangeRowViewModel { Entry: { } entry } || !ChangeRowCanDelete(entry))
        {
            SetStatus("Nessun file idoneo per SVN Delete.");
            return;
        }

        var message = $"SVN Delete di \"{entry.RelativePath}\": il file verrà eliminato dalla cartella di lavoro " +
                      "e rimosso da SVN al prossimo commit.";
        if (entry.Status is SvnStatusKind.Modified or SvnStatusKind.Replaced or SvnStatusKind.Conflicted or SvnStatusKind.Added)
            message += "\n\nATTENZIONE: il file ha modifiche locali non committate, che andranno perse.";

        if (!await _dialogs.ConfirmAsync(message, ct))
            return;

        try
        {
            SetStatus("Rimozione da SVN in corso...");
            // Senza --force svn rifiuta file con modifiche locali (E195006): l'utente è stato già avvisato sopra.
            var force = entry.Status is SvnStatusKind.Modified or SvnStatusKind.Replaced
                or SvnStatusKind.Conflicted or SvnStatusKind.Added;
            await _svnService.DeleteAsync([entry.FullPath], ct, force);
            await RefreshAsync(ct, reloadBranch: false, $"Elemento schedulato per delete: {entry.RelativePath}.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus($"Errore delete: {ex.Message}");
        }
    }

    private static bool ChangeRowCanDelete(ChangeEntry entry)
        => entry.Status is not (SvnStatusKind.Unversioned or SvnStatusKind.Unknown);

    /// <summary>SVN Revert su file, cartella o sezione. Chiede conferma elencando cosa verrà annullato.</summary>
    internal async Task RevertAsync(object? parameter, CancellationToken ct)
    {
        if (parameter is not ChangeRowViewModel row)
            return;

        // Percorsi espliciti (non la cartella): si annulla esattamente ciò che la lista mostra.
        var targets = row.Entries.Where(e => ChangeRowFactory.IsRevertable(e.Status)).ToList();
        if (targets.Count == 0)
        {
            SetStatus("Nessun elemento idoneo per SVN Revert.");
            return;
        }

        var scope = row.Kind switch
        {
            ChangeRowKind.Folder => $" nella cartella \"{row.FolderRelativePath}\"",
            ChangeRowKind.Section => $" ({ChangeRowFactory.SectionTitle(row.Section)})",
            _ => string.Empty,
        };
        var message = $"Annullare le modifiche locali su {targets.Count} elementi{scope}?\n\n{ListForConfirmation(targets)}\n\n" +
                      "Le modifiche andranno perse e l'operazione non è annullabile.";
        if (!await _dialogs.ConfirmAsync(message, ct))
            return;

        try
        {
            SetStatus("Revert in corso...");
            await _svnService.RevertAsync(targets.Select(e => e.FullPath).ToList(), ct);
            await RefreshAsync(ct, reloadBranch: false, $"Revert completato su {targets.Count} elementi.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus($"Errore revert: {ex.Message}");
        }
    }

    private static string ListForConfirmation(IReadOnlyList<ChangeEntry> entries)
    {
        var lines = entries.Take(MaxListedInConfirmation)
            .Select(e => $"• {e.RelativePath} ({StatusPresentation.DisplayName(e.Status, e.IsTreeConflict)})");
        var text = string.Join("\n", lines);
        return entries.Count > MaxListedInConfirmation
            ? $"{text}\n… e altri {entries.Count - MaxListedInConfirmation}"
            : text;
    }

    /// <summary>
    /// Risolve un conflitto. Conflitto di testo: l'utente sceglie Working / Mine-full / Theirs-full / Base.
    /// Conflitto ad albero: non ci sono marcatori, si chiede solo conferma di segnarlo risolto.
    /// </summary>
    internal async Task ResolveAsync(object? parameter, CancellationToken ct)
    {
        if (parameter is not ChangeRowViewModel { Entry: { Status: SvnStatusKind.Conflicted } entry })
        {
            SetStatus("Seleziona un file in conflitto da risolvere.");
            return;
        }

        SvnConflictChoice choice;
        if (entry.IsTreeConflict)
        {
            if (!await _dialogs.ConfirmAsync(
                    $"Conflitto ad albero su \"{entry.RelativePath}\" (rinomina/cancellazione). " +
                    "Sistema prima la struttura a mano, poi conferma per segnarlo come risolto.", ct))
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
            SetStatus("Risoluzione conflitto in corso...");
            await _svnService.ResolveAsync(entry.FullPath, choice, ct);
            await RefreshAsync(ct, reloadBranch: false, $"Conflitto risolto: {entry.RelativePath}");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus($"Errore resolve: {ex.Message}");
        }
    }

    /// <summary>
    /// Aggiunge alla ignore list. Su una cartella (che raggruppa elementi non versionati) ignora
    /// ciascun elemento non versionato contenuto, dopo conferma.
    /// </summary>
    internal async Task IgnoreAsync(object? parameter, CancellationToken ct)
    {
        if (parameter is not ChangeRowViewModel row)
            return;

        var targets = row.Entries.Where(e => e.Status == SvnStatusKind.Unversioned).ToList();
        if (targets.Count == 0)
        {
            SetStatus("Add to Ignore List è disponibile solo per elementi non versionati.");
            return;
        }

        if (row.Kind != ChangeRowKind.File
            && !await _dialogs.ConfirmAsync(
                $"Aggiungere {targets.Count} elementi non versionati alla ignore list (svn:ignore) " +
                $"delle rispettive cartelle?\n\n{ListForConfirmation(targets)}", ct))
            return;

        try
        {
            SetStatus("Aggiornamento ignore list in corso...");
            foreach (var entry in targets)
                await _svnService.AddToIgnoreListAsync(entry.FullPath, ct);

            await RefreshAsync(ct, reloadBranch: false, $"Aggiunti alla ignore list: {targets.Count} elementi.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus($"Errore ignore list: {ex.Message}");
        }
    }

    // ── Diff ─────────────────────────────────────────────────────────

    /// <summary>Diff di un file: prima TortoiseSVN se disponibile, altrimenti inline.</summary>
    internal async Task ShowFileDiffAsync(object? parameter, CancellationToken ct)
    {
        if (parameter is not ChangeRowViewModel { Entry: { } entry })
            return;

        try
        {
            if (await _svnService.OpenNativeDiffAsync(entry.FullPath, ct))
            {
                IsDiffVisible = false;
                SetStatus($"Diff aperto in TortoiseSVN: {entry.RelativePath}");
                return;
            }

            DiffTitle = $"Diff: {entry.RelativePath}";
            DiffContent = "Caricamento diff...";
            IsDiffVisible = true;

            var diff = await _svnService.GetFileDiffAsync(entry.FullPath, ct);
            DiffContent = string.IsNullOrWhiteSpace(diff)
                ? "(Nessuna differenza testuale — il file potrebbe essere binario o non versionato)"
                : diff;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            DiffContent = $"Errore: {ex.Message}";
            IsDiffVisible = true;
        }
    }

    /// <summary>Diff combinato dei file modificati dentro una cartella, mostrato inline.</summary>
    internal async Task ShowFolderDiffAsync(object? parameter, CancellationToken ct)
    {
        if (parameter is not ChangeRowViewModel row)
            return;

        var files = row.Entries.Where(e => e.Status != SvnStatusKind.Unversioned && !e.IsDirectory).ToList();
        if (files.Count == 0)
        {
            SetStatus("Nessuna modifica da confrontare in questa cartella.");
            return;
        }

        try
        {
            DiffTitle = $"Diff cartella: {(row.FolderRelativePath.Length > 0 ? row.FolderRelativePath : row.Name)} ({files.Count} file)";
            DiffContent = "Caricamento diff...";
            IsDiffVisible = true;

            var parts = new List<string>();
            foreach (var file in files.Take(MaxFolderDiffFiles))
            {
                var diff = await _svnService.GetFileDiffAsync(file.FullPath, ct);
                if (!string.IsNullOrWhiteSpace(diff))
                    parts.Add(diff.TrimEnd());
            }

            if (files.Count > MaxFolderDiffFiles)
                parts.Add($"… altri {files.Count - MaxFolderDiffFiles} file omessi.");

            DiffContent = parts.Count == 0 ? "(Nessuna differenza testuale)" : string.Join("\n\n", parts);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            DiffContent = $"Errore: {ex.Message}";
        }
    }

    private Task CloseDiffAsync(object? parameter, CancellationToken ct)
    {
        CloseDiff();
        return Task.CompletedTask;
    }

    private void CloseDiff()
    {
        IsDiffVisible = false;
        DiffContent = string.Empty;
        DiffTitle = string.Empty;
    }

    // ── Utilità ──────────────────────────────────────────────────────

    /// <summary>Imposta un messaggio d'azione: resta visibile finché non lo sostituisce un altro (i refresh automatici non lo toccano).</summary>
    private void SetStatus(string message)
    {
        _statusIsAuto = false;
        StatusMessage = message;
    }

    private static string NormalizeStatusPath(string statusPath, string workingCopyPath)
    {
        if (string.IsNullOrWhiteSpace(statusPath) || statusPath == ".")
            return workingCopyPath;

        return Path.IsPathRooted(statusPath)
            ? statusPath
            : Path.GetFullPath(Path.Combine(workingCopyPath, statusPath));
    }
}
