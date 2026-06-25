using System.IO;
using System.ComponentModel;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.ProjectSystem.Query;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;
using SVNAssist.Services;

namespace SVNAssist.ToolWindows;

/// <summary>
/// Tool window "SVN Status" — mostra la lista dei file modificati nella working copy.
/// </summary>
/// <remarks>
/// Nel nuovo modello VisualStudio.Extensibility, una tool window:
/// - Eredita da <see cref="ToolWindow"/> (non da <c>ToolWindowPane</c> del vecchio modello)
/// - Usa <c>[VisualStudioContribution]</c> per registrarsi automaticamente
/// - Restituisce un <see cref="IRemoteUserControl"/> come contenuto (Remote UI)
/// - Gira out-of-process — il crash della tool window non fa crashare VS
///
/// <see cref="ToolWindowConfiguration"/> definisce come VS gestisce la finestra:
/// - Placement: dove appare di default. Si usa <c>Floating</c> per garantire
///   visibilità immediata — <c>DocumentWell</c> può nascondere la finestra
///   dietro ai tab dei documenti, causando il bug "nessun output visibile".
///
/// Il parametro <see cref="Services.Settings.SvnAssistCategoryObserver"/> viene
/// iniettato dal container DI — registrato da <c>AddSettingsObservers()</c>
/// in <see cref="SVNAssistExtension.InitializeServices"/>.
/// </remarks>
[VisualStudioContribution]
internal class SvnStatusToolWindow : ToolWindow
{
    private readonly SvnStatusToolWindowData _dataContext;
    private readonly PeriodicTimer _solutionWatcherTimer = new(TimeSpan.FromSeconds(5));
    private readonly PeriodicTimer _liveRefreshTimer = new(TimeSpan.FromSeconds(1));
    private readonly SemaphoreSlim _liveRefreshLock = new(1, 1);
    private CancellationTokenSource? _watcherCts;
    private Task? _watcherTask;
    private Task? _liveRefreshTask;
    private FileSystemWatcher? _workingCopyWatcher;
    private string _watchedRoot = string.Empty;
    private volatile bool _pendingFsRefresh;
    private DateTime _lastFsEventUtc = DateTime.MinValue;

    public SvnStatusToolWindow(
        VisualStudioExtensibility extensibility,
        Services.Settings.SvnAssistCategoryObserver settingsObserver)
        : base(extensibility)
    {
        Title = "SVN Changes";

        // Questa tool window funge da composition root: crea i servizi concreti e li inietta
        // nel ViewModel, che dipende solo dalle astrazioni (ISvnService / IAiServiceFactory).
        // Se svn.exe non è installato passiamo comunque un path placeholder: le operazioni
        // falliranno in modo gestito (catch nel ViewModel) e la verifica all'apertura
        // (EnsureSvnAvailableAsync) proporrà l'installazione.
        var settingsService = new SettingsService(settingsObserver);
        var processRunner = new ProcessRunner();
        var svnService = new SvnService(SvnLocator.FindSvn() ?? "svn.exe", processRunner);
        var aiServiceFactory = new AiServiceFactory(settingsService, new GitHubTokenResolver(processRunner));
        var aiSetupPrompt = new AiSetupPrompt(extensibility);
        _dataContext = new SvnStatusToolWindowData(settingsService, svnService, aiServiceFactory, aiSetupPrompt);
        _dataContext.PropertyChanged += OnDataContextPropertyChanged;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>ToolWindowPlacement.DockedTo</c> con <c>DocumentWellGuid</c> paga il rischio
    /// di finire nascosta nei tab — usiamo invece il placement laterale.
    /// Il pannello Git Changes usa il dock "Solution Explorer side" che corrisponde
    /// a <c>DocumentWell</c> con un ID specifico. Con <c>Floating</c> la finestra
    /// appare sempre visibile la prima volta, poi l'utente la ancora dove preferisce
    /// (tipicamente a destra, accanto a Solution Explorer, come Git Changes).
    /// </remarks>
    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.Floating,
        DockDirection = Dock.Right,
    };

    /// <summary>
    /// Restituisce il controllo Remote UI da mostrare nella tool window.
    /// Al primo caricamento, tenta l'auto-detect della working copy SVN
    /// dalla solution corrente e triggera un refresh automatico.
    /// </summary>
    public override async Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
    {
        // Verifica che svn.exe sia installato; se manca, propone l'installazione (una volta per sessione).
        await SvnSetupAssistant.EnsureSvnAvailableAsync(Extensibility, cancellationToken);

        // Auto-detect: cerca la root SVN dalla directory della solution aperta
        await RefreshAutoDetectFromSolutionAsync(cancellationToken);
        ConfigureWorkingCopyWatcher(_dataContext.WorkingCopyPath);
        StartSolutionWatcher();
        StartLiveRefreshWatcher();

        return new SvnStatusToolWindowControl(_dataContext);
    }

    /// <summary>
    /// Tenta di rilevare automaticamente la root SVN dalla solution aperta in VS.
    /// </summary>
    /// <remarks>
    /// Usa la Project System Query API per ottenere la directory della solution,
    /// poi naviga verso l'alto nel filesystem cercando <c>.svn</c>.
    /// Se la trova, popola <see cref="SvnStatusToolWindowData.WorkingCopyPath"/>
    /// e triggera il refresh automatico.
    /// </remarks>
    private async Task RefreshAutoDetectFromSolutionAsync(CancellationToken ct)
    {
        try
        {
            // Query della solution corrente per ottenere la directory
            var result = await Extensibility.Workspaces().QuerySolutionAsync(
                solution => solution.With(s => s.Directory),
                ct);

            var solutionDir = result.FirstOrDefault()?.Directory;
            var svnRoot = !string.IsNullOrWhiteSpace(solutionDir)
                ? SvnService.FindSvnRoot(solutionDir)
                : null;
            await _dataContext.ApplyAutoDetectedWorkingCopyAsync(svnRoot, ct);
        }
        catch
        {
            // Se la query fallisce (es. nessuna solution aperta), non blocchiamo
            _dataContext.StatusMessage = "Inserisci il percorso della working copy e premi Refresh.";
        }
    }

    private void StartSolutionWatcher()
    {
        if (_watcherTask is not null)
            return;

        _watcherCts = new CancellationTokenSource();
        _watcherTask = RunSolutionWatcherAsync(_watcherCts.Token);
    }

    private void StartLiveRefreshWatcher()
    {
        if (_liveRefreshTask is not null)
            return;

        _watcherCts ??= new CancellationTokenSource();
        _liveRefreshTask = RunLiveRefreshWatcherAsync(_watcherCts.Token);
    }

    /// <summary>
    /// Polling asincrono del contesto solution: quando cambia solution/cartella,
    /// aggiorna automaticamente la working copy e la lista SVN.
    /// </summary>
    /// <remarks>
    /// VisualStudio.Extensibility out-of-process non espone ancora un evento
    /// affidabile di "solution changed" equivalente al vecchio modello.
    /// Usiamo quindi un polling leggero tramite <see cref="PeriodicTimer"/> +
    /// <see cref="WorkspacesExtensibility.QuerySolutionAsync{T}"/>.
    /// </remarks>
    private async Task RunSolutionWatcherAsync(CancellationToken ct)
    {
        try
        {
            while (await _solutionWatcherTimer.WaitForNextTickAsync(ct))
            {
                await RefreshAutoDetectFromSolutionAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // normale durante Dispose
        }
    }

    /// <summary>
    /// Polling leggero del filesystem con debounce per aggiornare lo stato SVN
    /// quando cambiano file/cartelle nella working copy (modifica, copia, rename, delete).
    /// </summary>
    private async Task RunLiveRefreshWatcherAsync(CancellationToken ct)
    {
        try
        {
            while (await _liveRefreshTimer.WaitForNextTickAsync(ct))
            {
                if (!_pendingFsRefresh)
                    continue;

                if ((DateTime.UtcNow - _lastFsEventUtc) < TimeSpan.FromMilliseconds(800))
                    continue;

                _pendingFsRefresh = false;

                if (!await _liveRefreshLock.WaitAsync(0, ct))
                    continue;

                try
                {
                    if (!string.IsNullOrWhiteSpace(_dataContext.WorkingCopyPath))
                        await _dataContext.RefreshAsync(ct);
                }
                finally
                {
                    _liveRefreshLock.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normale durante Dispose
        }
    }

    private void OnDataContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SvnStatusToolWindowData.WorkingCopyPath))
        {
            ConfigureWorkingCopyWatcher(_dataContext.WorkingCopyPath);
        }
    }

    private void ConfigureWorkingCopyWatcher(string? workingCopyPath)
    {
        var normalized = string.IsNullOrWhiteSpace(workingCopyPath)
            ? string.Empty
            : Path.GetFullPath(workingCopyPath);

        if (string.Equals(_watchedRoot, normalized, StringComparison.OrdinalIgnoreCase))
            return;

        _workingCopyWatcher?.Dispose();
        _workingCopyWatcher = null;
        _watchedRoot = normalized;

        if (string.IsNullOrWhiteSpace(normalized) || !Directory.Exists(normalized))
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

        watcher.Changed += OnWorkingCopyFsChanged;
        watcher.Created += OnWorkingCopyFsChanged;
        watcher.Deleted += OnWorkingCopyFsChanged;
        watcher.Renamed += OnWorkingCopyFsChanged;
        watcher.EnableRaisingEvents = true;
        _workingCopyWatcher = watcher;
    }

    private void OnWorkingCopyFsChanged(object sender, FileSystemEventArgs e)
    {
        if (IsSvnAdminPath(e.FullPath))
            return;

        _lastFsEventUtc = DateTime.UtcNow;
        _pendingFsRefresh = true;
    }

    private static bool IsSvnAdminPath(string fullPath)
    {
        var marker = $"{Path.DirectorySeparatorChar}.svn{Path.DirectorySeparatorChar}";
        return fullPath.Contains(marker, StringComparison.OrdinalIgnoreCase)
            || fullPath.EndsWith($"{Path.DirectorySeparatorChar}.svn", StringComparison.OrdinalIgnoreCase);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dataContext.PropertyChanged -= OnDataContextPropertyChanged;
            _watcherCts?.Cancel();
            _solutionWatcherTimer.Dispose();
            _liveRefreshTimer.Dispose();
            _watcherCts?.Dispose();
            _workingCopyWatcher?.Dispose();
            _liveRefreshLock.Dispose();
        }

        base.Dispose(disposing);
    }
}
