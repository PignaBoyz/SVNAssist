using System.Net.Http;
using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>
/// Azioni con effetti sul sistema lanciate dai pulsanti della checklist (iniettabili per i test).
/// I default non fanno nulla: la composizione reale (<c>BackendServices.CreateDefault</c>) li collega all'host.
/// </summary>
internal sealed class PrerequisiteActions
{
    /// <summary>Lancia un comando in un terminale visibile. <c>false</c> se non e' stato possibile.</summary>
    public Func<ConsoleCommand, string?, Task<bool>> LaunchConsole { get; init; } = (_, _) => Task.FromResult(false);

    /// <summary>Apre un URL nel browser.</summary>
    public Func<string, Task> OpenUrl { get; init; } = _ => Task.CompletedTask;

    /// <summary>Scarica il runtime di Copilot.</summary>
    public Func<CancellationToken, Task> DownloadCopilotRuntime { get; init; } = async ct =>
    {
        using var http = new HttpClient();
        await CopilotRuntimeInstaller.EnsureInstalledAsync(http, ct);
    };

    /// <summary>Percorso di claude.exe (per "Accedi a Claude Code").</summary>
    public Func<string?> FindClaude { get; init; } = ClaudeCodeAiService.FindClaudeExecutable;
}

/// <summary>
/// Checklist dei prerequisiti: sezione comprimibile nel pannello, aperta in automatico al primo uso
/// se manca qualcosa di essenziale (svn). Fa parte dello snapshot di <see cref="ChangesStore"/>.
/// </summary>
internal sealed class PrereqStore
{
    private readonly PrerequisitesService _service;
    private readonly PrerequisiteActions _actions;
    private readonly object _lock = new();
    private bool _isOpen;
    private bool _autoOpened;
    private bool? _svnWasMissing;
    private int _busy;
    private string _summary = "Prerequisiti";
    private string _ai = string.Empty;
    private string _feedback = string.Empty;
    private DownloadDto? _download;
    private IReadOnlyList<PrereqItemDto> _items = [];

    public PrereqStore(PrerequisitesService service, PrerequisiteActions? actions = null)
    {
        _service = service;
        _actions = actions ?? new PrerequisiteActions();
    }

    /// <summary>Il servizio dei controlli (condiviso con la procedura guidata).</summary>
    internal PrerequisitesService Service => _service;

    internal static PrereqItemDto ToDto(PrerequisiteItem item) => new(
        item.Id,
        item.Group,
        item.Title,
        item.IsOk,
        item.Detail,
        item.IsOk ? string.Empty : item.Instructions,
        item.PrimaryAction ?? string.Empty,
        item.SecondaryAction ?? string.Empty);

    /// <summary>Qualcosa e' cambiato (per rimandare lo snapshot).</summary>
    public event Action? Changed;

    /// <summary>Chiamato quando svn.exe passa da mancante a presente (il pannello ricarica le modifiche).</summary>
    internal Func<CancellationToken, Task>? SvnBecameAvailable { get; set; }

    /// <summary>L'ultimo risultato dei controlli.</summary>
    internal PrerequisitesSnapshot? Snapshot { get; private set; }

    /// <summary>La sezione e' aperta.</summary>
    internal bool IsOpen
    {
        get { lock (_lock) return _isOpen; }
    }

    /// <summary>Esito dell'ultima azione (es. "Installazione avviata...").</summary>
    internal string Feedback
    {
        get { lock (_lock) return _feedback; }
        set { lock (_lock) _feedback = value; Changed?.Invoke(); }
    }

    /// <summary>Download in corso del runtime Copilot (<c>null</c> se nessuno).</summary>
    internal DownloadDto? Download
    {
        get { lock (_lock) return _download; }
    }

    /// <summary>Parte dello snapshot <c>prereq</c>.</summary>
    internal PrereqDto GetSnapshot()
    {
        lock (_lock)
            return new PrereqDto(_isOpen, _summary, _ai, _feedback, _items);
    }

    /// <summary>Apre/chiude la sezione.</summary>
    internal void Toggle()
    {
        lock (_lock)
            _isOpen = !_isOpen;
        Changed?.Invoke();
    }

    /// <summary>
    /// Controlla i prerequisiti; al primo controllo apre da sola la sezione se manca svn.
    /// </summary>
    internal async Task InitializeAsync(CancellationToken ct)
    {
        var snapshot = await RefreshAsync(ct);
        if (!_autoOpened)
        {
            _autoOpened = true;
            if (snapshot.SvnMissing)
            {
                lock (_lock)
                    _isOpen = true;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Riesegue i controlli e aggiorna la lista.</summary>
    internal async Task<PrerequisitesSnapshot> RefreshAsync(CancellationToken ct)
    {
        var snapshot = await _service.CheckAsync(ct);
        Snapshot = snapshot;

        var items = snapshot.Items.Select(ToDto).ToList();

        var ai = snapshot.HasAiProvider
            ? $"AI Provider: {snapshot.ConfiguredProvider}. Utilizzabili: {string.Join(" → ", snapshot.UsableProviders)}"
            : $"AI Provider: {snapshot.ConfiguredProvider}. Nessun provider AI utilizzabile: configurane almeno uno.";
        if (snapshot.ProviderNote.Length > 0)
            ai += "\n" + snapshot.ProviderNote;

        lock (_lock)
        {
            _items = items;
            _summary = snapshot.ProblemCount == 0 ? "Prerequisiti ✓" : $"Prerequisiti ({snapshot.ProblemCount} da sistemare)";
            _ai = ai;
        }

        var svnMissing = snapshot.SvnMissing;
        var recovered = _svnWasMissing == true && !svnMissing;
        _svnWasMissing = svnMissing;
        Changed?.Invoke();
        if (recovered && SvnBecameAvailable is { } callback)
            await callback(ct);

        return snapshot;
    }

    /// <summary>
    /// Esegue l'azione del pulsante principale o secondario dell'elemento <paramref name="id"/>
    /// (che deve esistere nell'ultimo controllo, o in <paramref name="rows"/> per la procedura guidata: l'id arriva dalla UI e viene rivalidato).
    /// </summary>
    internal async Task RunActionAsync(string id, bool primary, CancellationToken ct, IReadOnlyList<PrereqItemDto>? rows = null)
    {
        PrereqItemDto? row;
        lock (_lock)
            row = (rows ?? _items).FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));
        if (row is null)
            throw new ArgumentException($"Prerequisito sconosciuto: {id}");

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return;

        try
        {
            switch (row.Id, primary)
            {
                case (PrerequisiteIds.Svn, true):
                    await LaunchAsync(ConsoleCommand.InstallSvn, "Si è aperto un terminale con l'installazione di SVN: al termine premi Ricontrolla.");
                    break;
                case (PrerequisiteIds.Svn, false):
                    await _actions.OpenUrl(SvnEnvironment.DownloadUrl);
                    break;
                case (PrerequisiteIds.TortoiseSvn, _):
                    await _actions.OpenUrl(PrerequisitesService.TortoiseDownloadUrl);
                    break;
                case (PrerequisiteIds.GitHubCli, true):
                    await LaunchAsync(ConsoleCommand.InstallGitHubCli,
                        "Si è aperto un terminale con l'installazione di GitHub CLI: al termine premi Ricontrolla (se gh non viene trovato, riavvia VS).");
                    break;
                case (PrerequisiteIds.GitHubLogin, true):
                    await LaunchAsync(ConsoleCommand.GitHubLogin,
                        "Si è aperto un terminale: conferma il codice nel browser, poi premi Ricontrolla.");
                    break;
                case (PrerequisiteIds.ClaudeCode, true) when row.Primary.StartsWith("Accedi", StringComparison.Ordinal):
                    await LaunchAsync(ConsoleCommand.ClaudeLogin,
                        "Si è aperto un terminale con Claude Code: se richiesto esegui /login, poi premi Ricontrolla.");
                    break;
                case (PrerequisiteIds.ClaudeCode, true):
                    await LaunchAsync(ConsoleCommand.InstallClaudeCode,
                        "Si è aperto un terminale con l'installazione di Claude Code: al termine premi Ricontrolla.");
                    break;
                case (PrerequisiteIds.ClaudeCode, false):
                    await _actions.OpenUrl(PrerequisitesService.ClaudeCodeDocsUrl);
                    break;
                case (PrerequisiteIds.CopilotRuntime, true):
                    Feedback = "Download del runtime Copilot in corso (~100 MB)...";
                    lock (_lock)
                        _download = new DownloadDto(0, "Runtime GitHub Copilot (~100 MB)");
                    Changed?.Invoke();
                    try
                    {
                        await _actions.DownloadCopilotRuntime(ct);
                    }
                    finally
                    {
                        lock (_lock)
                            _download = null;
                    }

                    Feedback = "Runtime Copilot scaricato.";
                    await RefreshAsync(ct);
                    break;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Feedback = $"Errore: {ex.Message}";
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    internal async Task LaunchAsync(ConsoleCommand command, string successMessage)
    {
        var claude = command == ConsoleCommand.ClaudeLogin ? _actions.FindClaude() : null;
        Feedback = await _actions.LaunchConsole(command, claude)
            ? successMessage
            : "Impossibile aprire il terminale: usa i comandi indicati nelle istruzioni.";
    }
}
