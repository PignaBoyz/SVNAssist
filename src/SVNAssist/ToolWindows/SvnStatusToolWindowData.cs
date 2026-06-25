using System.IO;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.UI;
using SVNAssist.Services;

namespace SVNAssist.ToolWindows;

/// <summary>
/// Data context (ViewModel) per la tool window SVN Changes.
/// Layout ispirato a Git Changes di Visual Studio:
/// - Header con branch name e bottone Update
/// - Messaggio di commit inline con bottoni AI e Commit
/// - Lista file compatta con checkbox, stato, path relativo, bottone diff
/// - Pannello diff espandibile
/// </summary>
/// <remarks>
/// Nel modello VisualStudio.Extensibility, il data context deve:
/// - Avere l'attributo <c>[DataContract]</c> — i dati vengono serializzati
///   e inviati al processo di VS tramite un protocollo remoto
/// - Le proprietà bindabili devono avere <c>[DataMember]</c>
/// - Ereditare da <see cref="NotifyPropertyChangedObject"/> per il change notification
/// - Usare <see cref="IAsyncCommand"/> per i comandi (non <c>ICommand</c>)
///
/// Questo è il pattern "Remote UI": il XAML gira nel processo VS,
/// i dati e la logica girano nel processo dell'estensione (out-of-process).
/// </remarks>
[DataContract]
internal class SvnStatusToolWindowData : NotifyPropertyChangedObject
{
    private readonly ISvnService _svnService;
    private readonly IAiServiceFactory _aiServiceFactory;
    private readonly ISvnAssistSettings _settingsService;
    private readonly SemaphoreSlim _autoDetectLock = new(1, 1);

    private string _workingCopyPath = string.Empty;
    private string _statusMessage = "Caricamento in corso...";
    private string _fileListHeader = "Changes";
    private bool _showWorkingCopyInput;
    private string _diffContent = string.Empty;
    private string _diffTitle = string.Empty;
    private bool _isDiffVisible;
    private string _branchName = string.Empty;
    private string _commitMessage = string.Empty;
    private string _aiStatusMessage = string.Empty;
    private bool _isAiBusy;

    public SvnStatusToolWindowData(
        ISvnAssistSettings settingsService,
        ISvnService svnService,
        IAiServiceFactory aiServiceFactory)
    {
        _settingsService = settingsService;
        _svnService = svnService;
        _aiServiceFactory = aiServiceFactory;
        Files = [];
        RefreshCommand = new AsyncCommand(OnRefreshAsync);
        CommitCommand = new AsyncCommand(OnCommitAsync);
        UpdateCommand = new AsyncCommand(OnUpdateAsync);
        GenerateAiCommand = new AsyncCommand(OnGenerateAiAsync);
        ImproveAiCommand = new AsyncCommand(OnImproveAiAsync);
        ShowFileDiffCommand = new AsyncCommand(OnShowFileDiffAsync);
        CloseDiffCommand = new AsyncCommand(OnCloseDiffAsync);
        SelectAllCommand = new AsyncCommand(OnSelectAllAsync);
        UnselectAllCommand = new AsyncCommand(OnUnselectAllAsync);
        AddToSvnCommand = new AsyncCommand(OnAddToSvnAsync);
        DeleteFromSvnCommand = new AsyncCommand(OnDeleteFromSvnAsync);
        RevertSelectedCommand = new AsyncCommand(OnRevertSelectedAsync);
        ResolveConflictCommand = new AsyncCommand(OnResolveConflictAsync);
        AddToIgnoreListCommand = new AsyncCommand(OnAddToIgnoreListAsync);
    }

    // ── Header: Branch + Update ──────────────────────────────────────

    /// <summary>Nome del branch SVN corrente (es. "trunk", "branches/feature-x").</summary>
    [DataMember]
    public string BranchName
    {
        get => _branchName;
        set => SetProperty(ref _branchName, value);
    }

    // ── Inline commit message + AI ───────────────────────────────────

    /// <summary>Messaggio di commit scritto dall'utente (o generato dall'AI).</summary>
    [DataMember]
    public string CommitMessage
    {
        get => _commitMessage;
        set => SetProperty(ref _commitMessage, value);
    }

    /// <summary>Messaggio di stato dell'AI (es. "Generazione in corso...").</summary>
    [DataMember]
    public string AiStatusMessage
    {
        get => _aiStatusMessage;
        set => SetProperty(ref _aiStatusMessage, value);
    }

    /// <summary>Indica se un'operazione AI è in corso.</summary>
    [DataMember]
    public bool IsAiBusy
    {
        get => _isAiBusy;
        set => SetProperty(ref _isAiBusy, value);
    }

    // ── File list ────────────────────────────────────────────────────

    /// <summary>Lista dei file con stato modificato nella working copy.</summary>
    [DataMember]
    public ObservableList<FileStatusItem> Files { get; }

    /// <summary>Percorso della working copy SVN da analizzare.</summary>
    [DataMember]
    public string WorkingCopyPath
    {
        get => _workingCopyPath;
        set => SetProperty(ref _workingCopyPath, value);
    }

    /// <summary>Messaggio di stato mostrato in basso nella tool window.</summary>
    [DataMember]
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// Se true, mostra il campo di input manuale per il percorso della working copy.
    /// Nascosto quando il path è auto-rilevato.
    /// </summary>
    [DataMember]
    public bool ShowWorkingCopyInput
    {
        get => _showWorkingCopyInput;
        set => SetProperty(ref _showWorkingCopyInput, value);
    }

    /// <summary>Header della sezione file list con conteggio (es. "Changes (3)").</summary>
    [DataMember]
    public string FileListHeader
    {
        get => _fileListHeader;
        set => SetProperty(ref _fileListHeader, value);
    }

    // ── Diff panel ───────────────────────────────────────────────────

    /// <summary>Contenuto testuale del diff visualizzato nel pannello inferiore.</summary>
    [DataMember]
    public string DiffContent
    {
        get => _diffContent;
        set => SetProperty(ref _diffContent, value);
    }

    /// <summary>Titolo del pannello diff (nome del file).</summary>
    [DataMember]
    public string DiffTitle
    {
        get => _diffTitle;
        set => SetProperty(ref _diffTitle, value);
    }

    /// <summary>Se true, il pannello diff è visibile sotto la lista file.</summary>
    [DataMember]
    public bool IsDiffVisible
    {
        get => _isDiffVisible;
        set => SetProperty(ref _isDiffVisible, value);
    }

    // ── Comandi ──────────────────────────────────────────────────────

    /// <summary>Comando Refresh — ricarica lista file e branch name.</summary>
    [DataMember]
    public IAsyncCommand RefreshCommand { get; }

    /// <summary>Comando Commit — esegue il commit inline (senza dialog modale).</summary>
    [DataMember]
    public IAsyncCommand CommitCommand { get; }

    /// <summary>Comando Update — esegue svn update sulla working copy.</summary>
    [DataMember]
    public IAsyncCommand UpdateCommand { get; }

    /// <summary>Genera il messaggio di commit con AI analizzando il diff.</summary>
    [DataMember]
    public IAsyncCommand GenerateAiCommand { get; }

    /// <summary>Migliora il messaggio di commit esistente con AI.</summary>
    [DataMember]
    public IAsyncCommand ImproveAiCommand { get; }

    /// <summary>Mostra il diff di un singolo file.</summary>
    [DataMember]
    public IAsyncCommand ShowFileDiffCommand { get; }

    /// <summary>Chiude il pannello diff.</summary>
    [DataMember]
    public IAsyncCommand CloseDiffCommand { get; }

    /// <summary>Seleziona tutti i file.</summary>
    [DataMember]
    public IAsyncCommand SelectAllCommand { get; }

    /// <summary>Deseleziona tutti i file.</summary>
    [DataMember]
    public IAsyncCommand UnselectAllCommand { get; }

    /// <summary>Aggiunge a SVN i file selezionati o il file cliccato.</summary>
    [DataMember]
    public IAsyncCommand AddToSvnCommand { get; }

    /// <summary>Rimuove da SVN i file selezionati o il file cliccato.</summary>
    [DataMember]
    public IAsyncCommand DeleteFromSvnCommand { get; }

    /// <summary>Esegue revert sui file selezionati o sul file cliccato.</summary>
    [DataMember]
    public IAsyncCommand RevertSelectedCommand { get; }

    /// <summary>Risoluzione conflitto sul file cliccato.</summary>
    [DataMember]
    public IAsyncCommand ResolveConflictCommand { get; }

    /// <summary>Aggiunge il file/cartella alla ignore list SVN.</summary>
    [DataMember]
    public IAsyncCommand AddToIgnoreListCommand { get; }

    // ── Metodi interni ───────────────────────────────────────────────

    /// <summary>
    /// Esegue il refresh in modo programmatico (senza <see cref="IClientContext"/>).
    /// Usato dalla tool window per il refresh automatico all'apertura.
    /// </summary>
    internal Task RefreshAsync(CancellationToken ct) => OnRefreshAsync(null, ct);

    /// <summary>
    /// Applica il risultato dell'auto-detect della working copy e aggiorna la UI.
    /// </summary>
    internal async Task ApplyAutoDetectedWorkingCopyAsync(string? svnRoot, CancellationToken ct)
    {
        await _autoDetectLock.WaitAsync(ct);
        try
        {
            if (string.IsNullOrWhiteSpace(svnRoot))
            {
                WorkingCopyPath = string.Empty;
                BranchName = string.Empty;
                ShowWorkingCopyInput = true;
                Files.Clear();
                FileListHeader = "Changes (0)";
                IsDiffVisible = false;
                DiffTitle = string.Empty;
                DiffContent = string.Empty;
                StatusMessage = "La solution corrente non è una working copy SVN.";
                return;
            }

            if (string.Equals(WorkingCopyPath, svnRoot, StringComparison.OrdinalIgnoreCase) && Files.Count > 0)
                return;

            WorkingCopyPath = svnRoot;
            ShowWorkingCopyInput = false;
            StatusMessage = "Working copy SVN rilevata automaticamente, aggiornamento...";
            await RefreshAsync(ct);
        }
        finally
        {
            _autoDetectLock.Release();
        }
    }

    // ── Handler dei comandi ──────────────────────────────────────────

    /// <summary>
    /// Handler del comando Refresh.
    /// Chiama <c>svn status --xml</c> e popola la lista dei file,
    /// poi carica il branch name corrente.
    /// </summary>
    private async Task OnRefreshAsync(object? parameter, CancellationToken ct)
    {
        // Se il campo è vuoto, prova a leggere il percorso dalle impostazioni di VS
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

        try
        {
            StatusMessage = "Caricamento...";

            // Carica branch name e status in parallelo
            var branchTask = _svnService.GetBranchNameAsync(WorkingCopyPath, ct);
            var statusTask = _svnService.GetStatusAsync(WorkingCopyPath, ct);

            await Task.WhenAll(branchTask, statusTask);

            BranchName = await branchTask;
            var statuses = await statusTask;

            Files.Clear();
            foreach (var status in statuses)
            {
                var normalizedFullPath = NormalizeStatusPath(status.Path, WorkingCopyPath);

                // Calcola il path relativo rispetto alla working copy root
                var relativePath = string.Equals(normalizedFullPath, WorkingCopyPath, StringComparison.OrdinalIgnoreCase)
                    ? "."
                    : Path.GetRelativePath(WorkingCopyPath, normalizedFullPath);

                // Abbrevia lo stato (M, A, D, ?, !, R) per un layout compatto
                var shortStatus = GetShortStatus(status.Status);

                Files.Add(new FileStatusItem
                {
                    FullPath = normalizedFullPath,
                    Path = relativePath,
                    Status = shortStatus,
                    Action = GetStatusDisplayName(status.Status),
                    StatusColor = GetStatusColor(status.Status),
                    IsSelected = status.Status != Models.SvnStatusKind.Unversioned,
                    CanAddToSvn = status.Status == Models.SvnStatusKind.Unversioned,
                    CanDeleteFromSvn = status.Status != Models.SvnStatusKind.Unversioned && status.Status != Models.SvnStatusKind.Unknown,
                    CanRevert = status.Status is Models.SvnStatusKind.Modified
                        or Models.SvnStatusKind.Added
                        or Models.SvnStatusKind.Deleted
                        or Models.SvnStatusKind.Conflicted
                        or Models.SvnStatusKind.Missing
                        or Models.SvnStatusKind.Replaced,
                    CanResolveConflict = status.Status == Models.SvnStatusKind.Conflicted,
                    CanAddToIgnoreList = status.Status == Models.SvnStatusKind.Unversioned,
                });
            }

            FileListHeader = $"Changes ({Files.Count})";
            StatusMessage = Files.Count == 0
                ? "Nessuna modifica."
                : $"{Files.Count} file modificati";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore: {ex.Message}";
            ShowWorkingCopyInput = true;
        }
    }

    /// <summary>
    /// Handler del comando Commit — esegue il commit inline senza dialog modale.
    /// Usa il messaggio scritto nella TextBox della tool window.
    /// </summary>
    private async Task OnCommitAsync(object? parameter, CancellationToken ct)
    {
        var selectedFiles = Files.Where(f => f.IsSelected).ToList();

        if (selectedFiles.Count == 0)
        {
            StatusMessage = "Seleziona almeno un file per il commit.";
            return;
        }

        if (string.IsNullOrWhiteSpace(CommitMessage))
        {
            StatusMessage = "Scrivi un messaggio di commit.";
            return;
        }

        try
        {
            StatusMessage = "Commit in corso...";

            // Costruisci i path completi per SvnService
            var fullPaths = selectedFiles.Select(ToFullPath).Distinct(StringComparer.OrdinalIgnoreCase);

            await _svnService.CommitAsync(fullPaths, CommitMessage.Trim(), ct);

            StatusMessage = $"Commit completato: {selectedFiles.Count} file.";
            CommitMessage = string.Empty;
            AiStatusMessage = string.Empty;

            // Refresh dopo il commit
            await OnRefreshAsync(null, ct);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore commit: {ex.Message}";
        }
    }

    /// <summary>
    /// Handler del comando Update — esegue svn update sulla working copy.
    /// </summary>
    private async Task OnUpdateAsync(object? parameter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(WorkingCopyPath))
        {
            StatusMessage = "Nessuna working copy configurata.";
            return;
        }

        try
        {
            StatusMessage = "Update in corso...";
            await _svnService.UpdateAsync(WorkingCopyPath, ct);
            StatusMessage = "Update completato.";

            // Refresh dopo l'update
            await OnRefreshAsync(null, ct);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore update: {ex.Message}";
        }
    }

    /// <summary>
    /// Handler per "Genera con AI" — analizza il diff e genera il commit message.
    /// </summary>
    private async Task OnGenerateAiAsync(object? parameter, CancellationToken ct)
    {
        IAiService? aiService = await _aiServiceFactory.CreateAsync(ct);

        if (aiService is null)
        {
            AiStatusMessage = "AI non configurata. Imposta API key in Tools > Options > SVNAssist.";
            return;
        }

        if (string.IsNullOrWhiteSpace(WorkingCopyPath))
        {
            AiStatusMessage = "Nessuna working copy configurata.";
            return;
        }

        try
        {
            IsAiBusy = true;
            AiStatusMessage = "Generazione in corso...";

            var diff = await _svnService.GetWorkingCopyDiffAsync(WorkingCopyPath, ct);

            if (string.IsNullOrWhiteSpace(diff))
            {
                AiStatusMessage = "Nessun diff disponibile.";
                return;
            }

            var language = await _settingsService.GetDefaultLanguageAsync(ct);
            CommitMessage = await aiService.GenerateCommitMessageAsync(diff, language, ct);

            AiStatusMessage = "Messaggio generato con AI.";
        }
        catch (Exception ex)
        {
            AiStatusMessage = $"Errore AI: {ex.Message}";
        }
        finally
        {
            IsAiBusy = false;
        }
    }

    /// <summary>
    /// Handler per "Migliora con AI" — migliora il messaggio di commit esistente.
    /// </summary>
    private async Task OnImproveAiAsync(object? parameter, CancellationToken ct)
    {
        IAiService? aiService = await _aiServiceFactory.CreateAsync(ct);

        if (aiService is null)
        {
            AiStatusMessage = "AI non configurata. Imposta API key in Tools > Options > SVNAssist.";
            return;
        }

        if (string.IsNullOrWhiteSpace(CommitMessage))
        {
            AiStatusMessage = "Scrivi prima una bozza del messaggio da migliorare.";
            return;
        }

        try
        {
            IsAiBusy = true;
            AiStatusMessage = "Miglioramento in corso...";

            var diff = await _svnService.GetWorkingCopyDiffAsync(WorkingCopyPath, ct);
            var language = await _settingsService.GetDefaultLanguageAsync(ct);
            CommitMessage = await aiService.ImproveCommitMessageAsync(CommitMessage, diff, language, ct);

            AiStatusMessage = "Messaggio migliorato.";
        }
        catch (Exception ex)
        {
            AiStatusMessage = $"Errore AI: {ex.Message}";
        }
        finally
        {
            IsAiBusy = false;
        }
    }

    /// <summary>
    /// Handler per mostrare il diff di un singolo file.
    /// </summary>
    private async Task OnShowFileDiffAsync(object? parameter, CancellationToken ct)
    {
        if (parameter is not FileStatusItem fileItem)
            return;

        try
        {
            var fullPath = Path.IsPathRooted(fileItem.FullPath)
                ? fileItem.FullPath
                : Path.Combine(WorkingCopyPath, fileItem.FullPath);

            // Priorità UX: se disponibile, apri il diff nativo (TortoiseSVN),
            // simile alla finestra diff integrata che l'utente si aspetta.
            var openedNativeDiff = await _svnService.OpenNativeDiffAsync(fullPath, ct);
            if (openedNativeDiff)
            {
                IsDiffVisible = false;
                StatusMessage = $"Diff aperto in TortoiseSVN: {fileItem.Path}";
                return;
            }

            // Fallback: mostra il diff testuale inline nella tool window.
            DiffTitle = $"Diff: {fileItem.Path}";
            DiffContent = "Caricamento diff...";
            IsDiffVisible = true;

            var diff = await _svnService.GetFileDiffAsync(fullPath, ct);

            DiffContent = string.IsNullOrWhiteSpace(diff)
                ? "(Nessuna differenza testuale — il file potrebbe essere binario o non versionato)"
                : diff;
            StatusMessage = $"Diff inline caricato: {fileItem.Path}";
        }
        catch (Exception ex)
        {
            DiffContent = $"Errore: {ex.Message}";
            IsDiffVisible = true;
        }
    }

    /// <summary>
    /// Handler per chiudere il pannello diff.
    /// </summary>
    private Task OnCloseDiffAsync(object? parameter, CancellationToken ct)
    {
        IsDiffVisible = false;
        DiffContent = string.Empty;
        DiffTitle = string.Empty;
        return Task.CompletedTask;
    }

    /// <summary>Seleziona tutti i file nella lista.</summary>
    private Task OnSelectAllAsync(object? parameter, CancellationToken ct)
    {
        foreach (var file in Files)
            file.IsSelected = true;
        return Task.CompletedTask;
    }

    /// <summary>Deseleziona tutti i file nella lista.</summary>
    private Task OnUnselectAllAsync(object? parameter, CancellationToken ct)
    {
        foreach (var file in Files)
            file.IsSelected = false;
        return Task.CompletedTask;
    }

    /// <summary>Aggiunge a SVN i file selezionati o il file target.</summary>
    private async Task OnAddToSvnAsync(object? parameter, CancellationToken ct)
    {
        var targets = GetSelectedOrSingleFilePaths(parameter)
            .Where(p => Files.Any(f =>
                string.Equals(ToFullPath(f), p, StringComparison.OrdinalIgnoreCase) &&
                f.CanAddToSvn))
            .ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "Nessun file idoneo per SVN Add.";
            return;
        }

        try
        {
            StatusMessage = "Aggiunta a SVN in corso...";
            await _svnService.AddAsync(targets, ct);
            StatusMessage = $"Aggiunti a SVN: {targets.Count} elementi.";
            await OnRefreshAsync(null, ct);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore add: {ex.Message}";
        }
    }

    /// <summary>Schedula la rimozione da SVN dei file selezionati o target.</summary>
    private async Task OnDeleteFromSvnAsync(object? parameter, CancellationToken ct)
    {
        var targets = GetSelectedOrSingleFilePaths(parameter)
            .Where(p => Files.Any(f =>
                string.Equals(ToFullPath(f), p, StringComparison.OrdinalIgnoreCase) &&
                f.CanDeleteFromSvn))
            .ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "Nessun file idoneo per SVN Delete.";
            return;
        }

        try
        {
            StatusMessage = "Rimozione da SVN in corso...";
            await _svnService.DeleteAsync(targets, ct);
            StatusMessage = $"Elementi schedulati per delete: {targets.Count}.";
            await OnRefreshAsync(null, ct);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore delete: {ex.Message}";
        }
    }

    /// <summary>Annulla modifiche locali dei file selezionati o target.</summary>
    private async Task OnRevertSelectedAsync(object? parameter, CancellationToken ct)
    {
        var targets = GetSelectedOrSingleFilePaths(parameter)
            .Where(p => Files.Any(f =>
                string.Equals(ToFullPath(f), p, StringComparison.OrdinalIgnoreCase) &&
                f.CanRevert))
            .ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "Nessun file idoneo per SVN Revert.";
            return;
        }

        try
        {
            StatusMessage = "Revert in corso...";
            await _svnService.RevertAsync(targets, ct);
            StatusMessage = $"Revert completato su {targets.Count} elementi.";
            await OnRefreshAsync(null, ct);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore revert: {ex.Message}";
        }
    }

    /// <summary>Marca come risolto un file in conflitto.</summary>
    private async Task OnResolveConflictAsync(object? parameter, CancellationToken ct)
    {
        if (parameter is not FileStatusItem fileItem)
        {
            StatusMessage = "Seleziona un file in conflitto da risolvere.";
            return;
        }

        if (!fileItem.CanResolveConflict)
        {
            StatusMessage = "Il file selezionato non è in conflitto.";
            return;
        }

        try
        {
            StatusMessage = "Risoluzione conflitto in corso...";
            await _svnService.ResolveAsync(ToFullPath(fileItem), ct);
            StatusMessage = $"Conflitto risolto: {fileItem.Path}";
            await OnRefreshAsync(null, ct);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore resolve: {ex.Message}";
        }
    }

    /// <summary>Aggiunge il file/cartella unversioned alla ignore list SVN della directory padre.</summary>
    private async Task OnAddToIgnoreListAsync(object? parameter, CancellationToken ct)
    {
        if (parameter is not FileStatusItem fileItem)
        {
            StatusMessage = "Seleziona un file o cartella da ignorare.";
            return;
        }

        if (!fileItem.CanAddToIgnoreList)
        {
            StatusMessage = "Add to Ignore List è disponibile solo per elementi unversioned.";
            return;
        }

        try
        {
            StatusMessage = "Aggiornamento ignore list in corso...";
            await _svnService.AddToIgnoreListAsync(ToFullPath(fileItem), ct);
            StatusMessage = $"Aggiunto alla ignore list: {fileItem.Path}";
            await OnRefreshAsync(null, ct);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore ignore list: {ex.Message}";
        }
    }

    /// <summary>
    /// Converte un <see cref="Models.SvnStatusKind"/> in un codice breve
    /// per la visualizzazione compatta nella lista file.
    /// </summary>
    private static string GetShortStatus(Models.SvnStatusKind status) => status switch
    {
        Models.SvnStatusKind.Modified => "M",
        Models.SvnStatusKind.Added => "A",
        Models.SvnStatusKind.Deleted => "D",
        Models.SvnStatusKind.Conflicted => "C",
        Models.SvnStatusKind.Unversioned => "?",
        Models.SvnStatusKind.Missing => "!",
        Models.SvnStatusKind.Replaced => "R",
        _ => "·",
    };

    /// <summary>
    /// Converte lo stato SVN in una descrizione esplicita, simile alla lista del commit SVN.
    /// </summary>
    private static string GetStatusDisplayName(Models.SvnStatusKind status) => status switch
    {
        Models.SvnStatusKind.Modified => "Modified",
        Models.SvnStatusKind.Added => "Added",
        Models.SvnStatusKind.Deleted => "Deleted",
        Models.SvnStatusKind.Conflicted => "Conflicted",
        Models.SvnStatusKind.Unversioned => "Unversioned",
        Models.SvnStatusKind.Missing => "Missing",
        Models.SvnStatusKind.Replaced => "Replaced",
        Models.SvnStatusKind.Normal => "Normal",
        _ => "Unknown",
    };

    /// <summary>
    /// Restituisce un colore Brush (come stringa) per lo stato SVN.
    /// I colori sono scelti per essere leggibili sia in tema chiaro che scuro.
    /// </summary>
    private static string GetStatusColor(Models.SvnStatusKind status) => status switch
    {
        Models.SvnStatusKind.Modified => "DodgerBlue",
        Models.SvnStatusKind.Added => "Green",
        Models.SvnStatusKind.Deleted => "Crimson",
        Models.SvnStatusKind.Conflicted => "Red",
        Models.SvnStatusKind.Unversioned => "SlateGray",
        Models.SvnStatusKind.Missing => "DarkOrange",
        Models.SvnStatusKind.Replaced => "MediumPurple",
        _ => "SlateGray",
    };

    private List<string> GetSelectedOrSingleFilePaths(object? parameter)
    {
        if (parameter is FileStatusItem item)
            return [ToFullPath(item)];

        var selected = Files.Where(f => f.IsSelected).Select(ToFullPath).ToList();
        if (selected.Count > 0)
            return selected;

        return [];
    }

    private string ToFullPath(FileStatusItem file) => Path.IsPathRooted(file.FullPath)
        ? file.FullPath
        : Path.Combine(WorkingCopyPath, file.FullPath);

    private static string NormalizeStatusPath(string statusPath, string workingCopyPath)
    {
        if (string.IsNullOrWhiteSpace(statusPath) || statusPath == ".")
            return workingCopyPath;

        return Path.IsPathRooted(statusPath)
            ? statusPath
            : Path.GetFullPath(Path.Combine(workingCopyPath, statusPath));
    }
}

/// <summary>
/// Rappresenta un file nella lista della tool window Status.
/// Ogni proprietà è bindabile dal XAML tramite Remote UI.
/// </summary>
[DataContract]
internal class FileStatusItem : NotifyPropertyChangedObject
{
    private string _fullPath = string.Empty;
    private string _path = string.Empty;
    private string _status = string.Empty;
    private string _action = string.Empty;
    private string _statusColor = "Gray";
    private bool _isSelected;
    private bool _canAddToSvn;
    private bool _canDeleteFromSvn = true;
    private bool _canRevert = true;
    private bool _canResolveConflict;
    private bool _canAddToIgnoreList;

    /// <summary>Percorso completo del file (usato internamente per svn diff/commit).</summary>
    [DataMember]
    public string FullPath
    {
        get => _fullPath;
        set => SetProperty(ref _fullPath, value);
    }

    /// <summary>Percorso del file relativo alla working copy (visualizzato nella UI).</summary>
    [DataMember]
    public string Path
    {
        get => _path;
        set => SetProperty(ref _path, value);
    }

    /// <summary>Stato SVN abbreviato (M, A, D, ?, !, R, C).</summary>
    [DataMember]
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>Descrizione esplicita dell'azione SVN (Added, Modified, Deleted, ...).</summary>
    [DataMember]
    public string Action
    {
        get => _action;
        set => SetProperty(ref _action, value);
    }

    /// <summary>
    /// Colore del testo dello stato SVN (nome colore WPF).
    /// M=CornflowerBlue, A=MediumSeaGreen, D=IndianRed, ?=Gray, !=DarkOrange, C=Crimson.
    /// </summary>
    [DataMember]
    public string StatusColor
    {
        get => _statusColor;
        set => SetProperty(ref _statusColor, value);
    }

    /// <summary>Se il file è selezionato per il commit.</summary>
    [DataMember]
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    [DataMember]
    public bool CanAddToSvn
    {
        get => _canAddToSvn;
        set => SetProperty(ref _canAddToSvn, value);
    }

    [DataMember]
    public bool CanDeleteFromSvn
    {
        get => _canDeleteFromSvn;
        set => SetProperty(ref _canDeleteFromSvn, value);
    }

    [DataMember]
    public bool CanRevert
    {
        get => _canRevert;
        set => SetProperty(ref _canRevert, value);
    }

    [DataMember]
    public bool CanResolveConflict
    {
        get => _canResolveConflict;
        set => SetProperty(ref _canResolveConflict, value);
    }

    [DataMember]
    public bool CanAddToIgnoreList
    {
        get => _canAddToIgnoreList;
        set => SetProperty(ref _canAddToIgnoreList, value);
    }

}
