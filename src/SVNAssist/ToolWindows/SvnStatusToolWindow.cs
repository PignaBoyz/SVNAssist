using System.IO;
using System.ComponentModel;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.ProjectSystem.Query;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;
using SVNAssist.Services;

namespace SVNAssist.ToolWindows;

/// <summary>
/// Tool window "SVN Changes": modifiche della working copy in stile Git Changes.
/// </summary>
/// <remarks>
/// Nel modello VisualStudio.Extensibility una tool window eredita da <see cref="ToolWindow"/>,
/// si registra con <c>[VisualStudioContribution]</c> e restituisce un <see cref="IRemoteUserControl"/>
/// (Remote UI): gira out-of-process, un crash non fa crashare VS.
///
/// Fa da composition root: crea i servizi concreti e li inietta nel ViewModel, che dipende solo
/// dalle astrazioni. Il refresh automatico usa un solo ciclo con debounce
/// (<see cref="RefreshDebouncer"/>) alimentato dal FileSystemWatcher, più un polling leggero
/// della solution (VS out-of-process non espone un evento affidabile di "solution changed").
/// </remarks>
[VisualStudioContribution]
internal class SvnStatusToolWindow : ToolWindow
{
    /// <summary>Quiete richiesta dopo l'ultimo evento del file system prima di rileggere lo stato.</summary>
    private static readonly TimeSpan RefreshQuietPeriod = TimeSpan.FromMilliseconds(800);

    private readonly SvnStatusToolWindowData _dataContext;
    private readonly PeriodicTimer _solutionWatcherTimer = new(TimeSpan.FromSeconds(5));
    private readonly RefreshDebouncer _debouncer;
    private CancellationTokenSource? _watcherCts;
    private Task? _solutionTask;
    private Task? _refreshTask;
    private readonly object _watcherLock = new();
    private FileSystemWatcher? _workingCopyWatcher;
    private string _watchedRoot = string.Empty;

    public SvnStatusToolWindow(
        VisualStudioExtensibility extensibility,
        Services.Settings.SvnAssistCategoryObserver settingsObserver)
        : base(extensibility)
    {
        Title = "SVN Changes";

        var settingsService = new SettingsService(settingsObserver);
        var processRunner = new ProcessRunner();
        var tokenResolver = new GitHubTokenResolver(processRunner);

        // Se svn.exe manca all'avvio, ReloadableSvnService lo ritrova appena viene installato.
        var svnService = new ReloadableSvnService();
        var aiServiceFactory = new AiServiceFactory(settingsService, tokenResolver, processRunner);
        var prerequisites = new PrerequisitesViewModel(new PrerequisitesService(
            settingsService, PrerequisiteProbes.CreateDefault(processRunner, tokenResolver)));

        _dataContext = new SvnStatusToolWindowData(
            settingsService, svnService, aiServiceFactory, new AiSetupPrompt(extensibility),
            new VsDialogService(extensibility), prerequisites);
        _dataContext.PropertyChanged += OnDataContextPropertyChanged;
        _debouncer = new RefreshDebouncer(RefreshQuietPeriod, ct =>
            string.IsNullOrWhiteSpace(_dataContext.WorkingCopyPath) ? Task.CompletedTask : _dataContext.RefreshAsync(ct));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>Floating</c> garantisce che la finestra sia visibile la prima volta (nel document well
    /// può sparire dietro ai tab); poi l'utente la ancora dove preferisce, di solito a destra
    /// accanto a Solution Explorer, come Git Changes.
    /// </remarks>
    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.Floating,
        DockDirection = Dock.Right,
    };

    /// <summary>
    /// Controlla i prerequisiti (la checklist si apre da sola se manca svn), rileva la working copy
    /// dalla solution aperta e avvia i watcher.
    /// </summary>
    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
    {
        // La finestra si mostra subito ("Caricamento in corso..."): sonda dei prerequisiti e primo svn status
        // (lenti su working copy grandi o rete lenta) girano in background e aggiornano il data context.
        StartBackgroundLoops();
        _ = InitializeInBackgroundAsync(_watcherCts!.Token);

        return Task.FromResult<IRemoteUserControl>(new SvnStatusToolWindowControl(_dataContext));
    }

    private async Task InitializeInBackgroundAsync(CancellationToken ct)
    {
        try
        {
            await _dataContext.InitializeAsync(ct);
            await RefreshAutoDetectFromSolutionAsync(ct);
            ConfigureWorkingCopyWatcher(_dataContext.WorkingCopyPath);
        }
        catch (OperationCanceledException)
        {
            // finestra chiusa durante il caricamento
        }
        catch (Exception ex)
        {
            _dataContext.ReportInitializationError(ex.Message);
        }
    }

    /// <summary>Rileva la root SVN dalla directory della solution aperta (risale cercando <c>.svn</c>).</summary>
    private async Task RefreshAutoDetectFromSolutionAsync(CancellationToken ct)
    {
        try
        {
            var result = await Extensibility.Workspaces().QuerySolutionAsync(
                solution => solution.With(s => s.Directory),
                ct);

            var solutionDir = result.FirstOrDefault()?.Directory;
            var svnRoot = !string.IsNullOrWhiteSpace(solutionDir)
                ? SvnService.FindSvnRoot(solutionDir)
                : null;
            await _dataContext.ApplyAutoDetectedWorkingCopyAsync(svnRoot, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Nessuna solution aperta: non blocchiamo, si può indicare il percorso a mano.
            _dataContext.NotifyNoSolution();
        }
    }

    private void StartBackgroundLoops()
    {
        if (_solutionTask is not null)
            return;

        _watcherCts = new CancellationTokenSource();
        _solutionTask = RunSolutionWatcherAsync(_watcherCts.Token);
        _refreshTask = _debouncer.RunAsync(_watcherCts.Token);
    }

    /// <summary>Quando cambia solution/cartella aggiorna la working copy (refresh solo se la root cambia).</summary>
    private async Task RunSolutionWatcherAsync(CancellationToken ct)
    {
        try
        {
            while (await _solutionWatcherTimer.WaitForNextTickAsync(ct))
                await RefreshAutoDetectFromSolutionAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // normale durante Dispose
        }
    }

    private void OnDataContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SvnStatusToolWindowData.WorkingCopyPath))
            ConfigureWorkingCopyWatcher(_dataContext.WorkingCopyPath);
    }

    private void ConfigureWorkingCopyWatcher(string? workingCopyPath)
    {
        string normalized;
        try
        {
            normalized = string.IsNullOrWhiteSpace(workingCopyPath) ? string.Empty : Path.GetFullPath(workingCopyPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalized = string.Empty; // percorso digitato a metà o non valido
        }

        // Il watcher ricorsivo parte solo su una working copy vera (esiste e ha una root .svn): un percorso digitato
        // a metà, o una cartella enorme qualsiasi, non deve sorvegliare un intero disco.
        if (normalized.Length > 0 && (!Directory.Exists(normalized) || SvnService.FindSvnRoot(normalized) is null))
            normalized = string.Empty;

        lock (_watcherLock)
        {
            if (string.Equals(_watchedRoot, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            _workingCopyWatcher?.Dispose();
            _workingCopyWatcher = null;
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

        watcher.Changed += OnWorkingCopyFsChanged;
        watcher.Created += OnWorkingCopyFsChanged;
        watcher.Deleted += OnWorkingCopyFsChanged;
        watcher.Renamed += OnWorkingCopyFsChanged;
        // Overflow del buffer (molti file cambiati insieme): gli eventi persi si recuperano con un refresh completo.
        watcher.Error += (_, _) => _debouncer.Request();
        watcher.EnableRaisingEvents = true;
        _workingCopyWatcher = watcher;
        }
    }

    private void OnWorkingCopyFsChanged(object sender, FileSystemEventArgs e)
    {
        // bin/ obj/ .vs/ .svn/ cambiano di continuo senza toccare lo stato SVN.
        if (WorkingCopyPathFilter.IsNoise(_watchedRoot, e.FullPath))
            return;

        _debouncer.Request();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dataContext.PropertyChanged -= OnDataContextPropertyChanged;
            _watcherCts?.Cancel();
            _solutionWatcherTimer.Dispose();
            _watcherCts?.Dispose();
            _workingCopyWatcher?.Dispose();
        }

        base.Dispose(disposing);
    }
}
