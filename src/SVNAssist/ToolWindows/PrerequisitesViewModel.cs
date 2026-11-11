using System.Net.Http;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;
using SVNAssist.Services;

namespace SVNAssist.ToolWindows;

/// <summary>Una riga della checklist dei prerequisiti.</summary>
[DataContract]
internal sealed class PrerequisiteRowViewModel : NotifyPropertyChangedObject
{
    private string _statusText = string.Empty;
    private string _statusColor = "Gray";
    private string _title = string.Empty;
    private string _detail = string.Empty;
    private string _instructions = string.Empty;
    private string _primaryLabel = string.Empty;
    private string _secondaryLabel = string.Empty;
    private string _groupHeader = string.Empty;

    /// <summary>Identificatore (<see cref="PrerequisiteIds"/>).</summary>
    [DataMember]
    public string Id { get; set; } = string.Empty;

    /// <summary>Titolo del gruppo, valorizzato solo sulla prima riga di ogni gruppo.</summary>
    [DataMember]
    public string GroupHeader
    {
        get => _groupHeader;
        set
        {
            SetProperty(ref _groupHeader, value);
            RaiseNotifyPropertyChangedEvent(nameof(HasGroupHeader));
        }
    }

    /// <summary>Mostra l'intestazione di gruppo.</summary>
    [DataMember]
    public bool HasGroupHeader => _groupHeader.Length > 0;

    /// <summary>✓ o ✗.</summary>
    [DataMember]
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>Verde se a posto, arancione se manca.</summary>
    [DataMember]
    public string StatusColor
    {
        get => _statusColor;
        set => SetProperty(ref _statusColor, value);
    }

    /// <summary>Nome del prerequisito.</summary>
    [DataMember]
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>Stato in una riga.</summary>
    [DataMember]
    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    /// <summary>Istruzioni esatte (vuote se il prerequisito è a posto).</summary>
    [DataMember]
    public string Instructions
    {
        get => _instructions;
        set
        {
            SetProperty(ref _instructions, value);
            RaiseNotifyPropertyChangedEvent(nameof(HasInstructions));
        }
    }

    /// <summary>Mostra il riquadro delle istruzioni.</summary>
    [DataMember]
    public bool HasInstructions => _instructions.Length > 0;

    /// <summary>Etichetta del pulsante principale.</summary>
    [DataMember]
    public string PrimaryLabel
    {
        get => _primaryLabel;
        set
        {
            SetProperty(ref _primaryLabel, value);
            RaiseNotifyPropertyChangedEvent(nameof(HasPrimary));
        }
    }

    /// <summary>Mostra il pulsante principale.</summary>
    [DataMember]
    public bool HasPrimary => _primaryLabel.Length > 0;

    /// <summary>Etichetta del pulsante secondario.</summary>
    [DataMember]
    public string SecondaryLabel
    {
        get => _secondaryLabel;
        set
        {
            SetProperty(ref _secondaryLabel, value);
            RaiseNotifyPropertyChangedEvent(nameof(HasSecondary));
        }
    }

    /// <summary>Mostra il pulsante secondario.</summary>
    [DataMember]
    public bool HasSecondary => _secondaryLabel.Length > 0;
}

/// <summary>
/// Azioni con effetti sul sistema lanciate dai pulsanti della checklist (iniettabili per i test).
/// </summary>
internal sealed class PrerequisiteActions
{
    /// <summary>Lancia un comando in una console visibile.</summary>
    public Func<ConsoleCommand, string?, bool> LaunchConsole { get; init; } = ConsoleLauncher.Launch;

    /// <summary>Apre un URL nel browser.</summary>
    public Action<string> OpenUrl { get; init; } = url =>
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // Best-effort: se il browser non si apre, l'URL resta nelle istruzioni.
        }
    };

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
/// ViewModel della checklist dei prerequisiti: sezione comprimibile nel pannello,
/// aperta in automatico al primo uso se manca qualcosa di essenziale (svn).
/// </summary>
[DataContract]
internal sealed class PrerequisitesViewModel : NotifyPropertyChangedObject
{
    private readonly PrerequisitesService _service;
    private readonly PrerequisiteActions _actions;
    private bool _isOpen;
    private bool _autoOpened;
    private bool? _svnWasMissing;
    private bool _isBusy;
    private string _summaryText = "Prerequisiti";
    private string _aiSummary = string.Empty;
    private string _feedback = string.Empty;

    public PrerequisitesViewModel(PrerequisitesService service, PrerequisiteActions? actions = null)
    {
        _service = service;
        _actions = actions ?? new PrerequisiteActions();
        Items = [];
        ToggleCommand = new AsyncCommand((_, _) =>
        {
            IsOpen = !IsOpen;
            return Task.CompletedTask;
        });
        CloseCommand = new AsyncCommand((_, _) =>
        {
            IsOpen = false;
            return Task.CompletedTask;
        });
        RecheckCommand = new AsyncCommand((_, ct) => RefreshAsync(ct));
        RunPrimaryCommand = new AsyncCommand((p, ct) => RunActionAsync(p, primary: true, ct));
        RunSecondaryCommand = new AsyncCommand((p, ct) => RunActionAsync(p, primary: false, ct));
    }

    /// <summary>Chiamato quando svn.exe passa da mancante a presente (il pannello ricarica le modifiche).</summary>
    internal Func<CancellationToken, Task>? SvnBecameAvailable { get; set; }

    /// <summary>L'ultimo snapshot calcolato.</summary>
    internal PrerequisitesSnapshot? Snapshot { get; private set; }

    /// <summary>La sezione è aperta.</summary>
    [DataMember]
    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    /// <summary>Testo del pulsante nel footer, es. "Prerequisiti (2 da sistemare)".</summary>
    [DataMember]
    public string SummaryText
    {
        get => _summaryText;
        set => SetProperty(ref _summaryText, value);
    }

    /// <summary>Provider AI impostato e provider oggi utilizzabili.</summary>
    [DataMember]
    public string AiSummary
    {
        get => _aiSummary;
        set => SetProperty(ref _aiSummary, value);
    }

    /// <summary>Esito dell'ultima azione (es. "Installazione avviata...").</summary>
    [DataMember]
    public string Feedback
    {
        get => _feedback;
        set
        {
            SetProperty(ref _feedback, value);
            RaiseNotifyPropertyChangedEvent(nameof(HasFeedback));
        }
    }

    /// <summary>Mostra la riga di feedback.</summary>
    [DataMember]
    public bool HasFeedback => _feedback.Length > 0;

    /// <summary>Elementi della checklist.</summary>
    [DataMember]
    public ObservableList<PrerequisiteRowViewModel> Items { get; }

    /// <summary>Apre/chiude la sezione.</summary>
    [DataMember]
    public IAsyncCommand ToggleCommand { get; }

    /// <summary>Chiude la sezione.</summary>
    [DataMember]
    public IAsyncCommand CloseCommand { get; }

    /// <summary>Ricontrolla i prerequisiti.</summary>
    [DataMember]
    public IAsyncCommand RecheckCommand { get; }

    /// <summary>Pulsante principale di una riga (parametro: la riga).</summary>
    [DataMember]
    public IAsyncCommand RunPrimaryCommand { get; }

    /// <summary>Pulsante secondario di una riga (parametro: la riga).</summary>
    [DataMember]
    public IAsyncCommand RunSecondaryCommand { get; }

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
                IsOpen = true;
        }
    }

    /// <summary>Riesegue i controlli e aggiorna la lista.</summary>
    internal async Task<PrerequisitesSnapshot> RefreshAsync(CancellationToken ct)
    {
        var snapshot = await _service.CheckAsync(ct);
        Snapshot = snapshot;

        var rows = new List<PrerequisiteRowViewModel>();
        string? lastGroup = null;
        foreach (var item in snapshot.Items)
        {
            rows.Add(new PrerequisiteRowViewModel
            {
                Id = item.Id,
                GroupHeader = item.Group != lastGroup ? item.Group : string.Empty,
                StatusText = item.IsOk ? "✓" : "✗",
                StatusColor = item.IsOk ? "MediumSeaGreen" : "DarkOrange",
                Title = item.Title,
                Detail = item.Detail,
                Instructions = item.IsOk ? string.Empty : item.Instructions,
                PrimaryLabel = item.PrimaryAction ?? string.Empty,
                SecondaryLabel = item.SecondaryAction ?? string.Empty,
            });
            lastGroup = item.Group;
        }

        Items.Clear();
        Items.AddRange(rows);

        SummaryText = snapshot.ProblemCount == 0
            ? "Prerequisiti ✓"
            : $"Prerequisiti ({snapshot.ProblemCount} da sistemare)";
        AiSummary = snapshot.HasAiProvider
            ? $"AI Provider: {snapshot.ConfiguredProvider}. Utilizzabili: {string.Join(" → ", snapshot.UsableProviders)}"
            : $"AI Provider: {snapshot.ConfiguredProvider}. Nessun provider AI utilizzabile: configurane almeno uno.";
        if (snapshot.ProviderNote.Length > 0)
            AiSummary += "\n" + snapshot.ProviderNote;

        var svnMissing = snapshot.SvnMissing;
        var recovered = _svnWasMissing == true && !svnMissing;
        _svnWasMissing = svnMissing;
        if (recovered && SvnBecameAvailable is { } callback)
            await callback(ct);

        return snapshot;
    }

    /// <summary>Esegue l'azione del pulsante principale o secondario della riga.</summary>
    internal async Task RunActionAsync(object? parameter, bool primary, CancellationToken ct)
    {
        if (parameter is not PrerequisiteRowViewModel row || _isBusy)
            return;

        try
        {
            _isBusy = true;
            switch (row.Id, primary)
            {
                case (PrerequisiteIds.Svn, true):
                    Launch(ConsoleCommand.InstallSvn, "Si è aperta una finestra con l'installazione di SVN: al termine premi Ricontrolla.");
                    break;
                case (PrerequisiteIds.Svn, false):
                    OpenUrl(SvnEnvironment.DownloadUrl);
                    break;
                case (PrerequisiteIds.TortoiseSvn, _):
                    OpenUrl(PrerequisitesService.TortoiseDownloadUrl);
                    break;
                case (PrerequisiteIds.GitHubCli, true):
                    Launch(ConsoleCommand.InstallGitHubCli,
                        "Si è aperta una finestra con l'installazione di GitHub CLI: al termine premi Ricontrolla (se gh non viene trovato, riavvia VS).");
                    break;
                case (PrerequisiteIds.GitHubLogin, true):
                    Launch(ConsoleCommand.GitHubLogin,
                        "Si è aperta una finestra: conferma il codice nel browser, poi premi Ricontrolla.");
                    break;
                case (PrerequisiteIds.ClaudeCode, true) when row.PrimaryLabel.StartsWith("Accedi", StringComparison.Ordinal):
                    Launch(ConsoleCommand.ClaudeLogin,
                        "Si è aperta una finestra con Claude Code: se richiesto esegui /login, poi premi Ricontrolla.");
                    break;
                case (PrerequisiteIds.ClaudeCode, true):
                    Launch(ConsoleCommand.InstallClaudeCode,
                        "Si è aperta una finestra PowerShell con l'installazione di Claude Code: al termine premi Ricontrolla.");
                    break;
                case (PrerequisiteIds.ClaudeCode, false):
                    OpenUrl(PrerequisitesService.ClaudeCodeDocsUrl);
                    break;
                case (PrerequisiteIds.CopilotRuntime, true):
                    Feedback = "Download del runtime Copilot in corso (~100 MB)...";
                    await _actions.DownloadCopilotRuntime(ct);
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
            _isBusy = false;
        }
    }

    private void Launch(ConsoleCommand command, string successMessage)
    {
        var claude = command == ConsoleCommand.ClaudeLogin ? _actions.FindClaude() : null;
        Feedback = _actions.LaunchConsole(command, claude)
            ? successMessage
            : "Impossibile avviare la finestra: usa i comandi indicati nelle istruzioni.";
    }

    private void OpenUrl(string url) => _actions.OpenUrl(url);
}
