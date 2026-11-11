using System.IO;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;
using SVNAssist.Services;

namespace SVNAssist.ToolWindows;

/// <summary>
/// Messaggio di commit con le azioni AI (Genera / Migliora) e la scelta della lingua nel pannello.
/// </summary>
/// <remarks>
/// Non conosce la lista file: riceve da chi lo crea le funzioni che restituiscono i percorsi
/// selezionati e la working copy, così resta testabile da solo.
/// </remarks>
[DataContract]
internal sealed class CommitMessageViewModel : NotifyPropertyChangedObject
{
    private const string Italian = "Italiano";
    private const string English = "English";
    private const string Auto = "Auto";

    private readonly ISvnService _svnService;
    private readonly IAiServiceFactory _aiServiceFactory;
    private readonly IAiSetupPrompt _aiSetupPrompt;
    private readonly ISvnAssistSettings _settings;
    private readonly Func<IReadOnlyList<string>> _selectedPaths;
    private readonly Func<string> _workingCopyPath;
    private readonly IDialogService? _dialogs;

    // Trasparenza: prima del primo invio al provider AI (e per ogni file sospetto nuovo) l'utente conferma.
    private bool _disclosureAccepted;
    private readonly HashSet<string> _confirmedSensitive = new(StringComparer.OrdinalIgnoreCase);

    private string _text = string.Empty;
    private string _aiStatus = string.Empty;
    private bool _isBusy;
    private int _aiRunning;
    private string _selectedLanguage = Auto;
    private bool _languageChosenByUser;
    private bool _languageLoaded;

    public CommitMessageViewModel(
        ISvnAssistSettings settings,
        ISvnService svnService,
        IAiServiceFactory aiServiceFactory,
        IAiSetupPrompt aiSetupPrompt,
        Func<IReadOnlyList<string>> selectedPaths,
        Func<string> workingCopyPath,
        IDialogService? dialogs = null)
    {
        _dialogs = dialogs;
        _settings = settings;
        _svnService = svnService;
        _aiServiceFactory = aiServiceFactory;
        _aiSetupPrompt = aiSetupPrompt;
        _selectedPaths = selectedPaths;
        _workingCopyPath = workingCopyPath;
        LanguageOptions = [Italian, English, Auto];
        GenerateCommand = new AsyncCommand(GenerateAsync);
        ImproveCommand = new AsyncCommand(ImproveAsync);
    }

    /// <summary>Testo del messaggio (scritto dall'utente o generato dall'AI).</summary>
    [DataMember]
    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    /// <summary>Stato dell'AI (es. "Generazione in corso...", "Messaggio generato con Claude Code.").</summary>
    [DataMember]
    public string AiStatus
    {
        get => _aiStatus;
        set => SetProperty(ref _aiStatus, value);
    }

    /// <summary>Un'operazione AI è in corso.</summary>
    [DataMember]
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            SetProperty(ref _isBusy, value);
            RaiseNotifyPropertyChangedEvent(nameof(IsIdle));
        }
    }

    /// <summary>Nessuna operazione AI in corso (abilita i pulsanti).</summary>
    [DataMember]
    public bool IsIdle => !_isBusy;

    /// <summary>Lingue selezionabili nel pannello.</summary>
    [DataMember]
    public ObservableList<string> LanguageOptions { get; }

    /// <summary>
    /// Lingua scelta nel pannello ("Italiano" / "English" / "Auto"). Vale per la sessione:
    /// non viene riscritta nelle impostazioni.
    /// </summary>
    [DataMember]
    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (string.IsNullOrEmpty(value))
                return;

            _languageChosenByUser = true;
            SetProperty(ref _selectedLanguage, value);
        }
    }

    /// <summary>Genera il messaggio dal diff dei file selezionati.</summary>
    [DataMember]
    public IAsyncCommand GenerateCommand { get; }

    /// <summary>Migliora la bozza scritta dall'utente.</summary>
    [DataMember]
    public IAsyncCommand ImproveCommand { get; }

    /// <summary>Legge la lingua iniziale dalle impostazioni (se l'utente non ha già scelto).</summary>
    internal async Task InitializeAsync(CancellationToken ct)
    {
        if (_languageLoaded)
            return;

        _languageLoaded = true;
        var stored = (await _settings.GetDefaultLanguageAsync(ct)).Trim().ToLowerInvariant();
        if (_languageChosenByUser)
            return;

        SetProperty(ref _selectedLanguage, stored switch
        {
            "italiano" => Italian,
            "english" => English,
            _ => Auto,
        }, nameof(SelectedLanguage));
    }

    /// <summary>Valore da passare a <see cref="IAiService"/>: "italiano", "english" o "auto".</summary>
    internal string LanguageForAi => _selectedLanguage switch
    {
        Italian => "italiano",
        English => "english",
        _ => "auto",
    };

    internal Task GenerateAsync(object? parameter, CancellationToken ct)
        => RunAiAsync(improve: false, ct);

    internal Task ImproveAsync(object? parameter, CancellationToken ct)
        => RunAiAsync(improve: true, ct);

    /// <summary>Flusso comune a Genera e Migliora: provider, file selezionati, diff minimo, chiamata AI.</summary>
    private async Task RunAiAsync(bool improve, CancellationToken ct)
    {
        // Un solo flusso AI alla volta: il doppio click non avvia due richieste né due guide di configurazione.
        if (Interlocked.CompareExchange(ref _aiRunning, 1, 0) != 0)
            return;

        try
        {
            IsBusy = true;
            await RunAiCoreAsync(improve, ct);
        }
        catch (OperationCanceledException)
        {
            // Mai silenzio: anche un annullamento (comando interrotto da VS, finestra chiusa) deve lasciare un feedback.
            AiStatus = ct.IsCancellationRequested ? "Operazione AI annullata." : "Errore AI: richiesta interrotta per timeout.";
        }
        catch (Exception ex)
        {
            AiStatus = $"Errore AI: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Volatile.Write(ref _aiRunning, 0);
        }
    }

    private async Task RunAiCoreAsync(bool improve, CancellationToken ct)
    {
        var ai = await _aiServiceFactory.CreateAsync(ct);
        if (ai is null)
        {
            AiStatus = "Nessun provider AI disponibile (Copilot, Claude o token GitHub).";
            // La guida compare a ogni click: se l'utente la chiude senza fare nulla riceve comunque questo messaggio.
            AiStatus += await _aiSetupPrompt.ShowMissingTokenHelpAsync(ct)
                ? " Completato il login o creato il token, premi di nuovo \"Genera con AI\"."
                : " Premi di nuovo \"Genera con AI\" per rivedere come configurarlo.";
            return;
        }

        if (improve && string.IsNullOrWhiteSpace(Text))
        {
            AiStatus = "Scrivi prima una bozza del messaggio da migliorare.";
            return;
        }

        var workingCopy = _workingCopyPath();
        if (string.IsNullOrWhiteSpace(workingCopy))
        {
            AiStatus = "Nessuna working copy configurata.";
            return;
        }

        // L'AI descrive solo i file selezionati (gli stessi che finiranno nel commit).
        var paths = _selectedPaths();
        if (paths.Count == 0)
        {
            AiStatus = improve
                ? "Seleziona almeno un file per migliorare il messaggio."
                : "Seleziona almeno un file per generare il messaggio.";
            return;
        }

        var chain = DescribeChain(ai);
        if (!await ConfirmDisclosureAsync(paths, chain, ct))
        {
            AiStatus = "Operazione annullata: nessun dato è stato inviato.";
            return;
        }

        AiStatus = (improve ? "Miglioramento" : "Generazione") + (chain is null ? " in corso..." : $" con {chain}...");

        var diff = await _svnService.GetDiffForAiAsync(paths, workingCopy, ct);
        if (!improve && string.IsNullOrWhiteSpace(diff))
        {
            AiStatus = "Nessun diff disponibile.";
            return;
        }

        // Tetto ai dati inviati: un file enorme spuntato per errore non deve diventare centinaia di KB e decine di richieste.
        var limited = DiffLimiter.Limit(diff);
        diff = limited.Diff;

        await InitializeAsync(ct);
        var language = LanguageForAi;
        var rules = CommitInstructions.Load(workingCopy);
        Text = improve
            ? await ai.ImproveCommitMessageAsync(Text, diff, language, ct, rules)
            : await ai.GenerateCommitMessageAsync(diff, language, ct, rules);

        var provider = DescribeProvider(ai);
        var done = improve ? "Messaggio migliorato" : "Messaggio generato con AI";
        AiStatus = provider is null ? $"{done}." : $"{done} ({provider}).";
        if (rules is not null)
            AiStatus += " Regole di commit del progetto applicate.";
        if (limited.WasTruncated)
            AiStatus += $" Attenzione: diff molto grande, inviata solo una parte (max {DiffLimiter.MaxTotalChars / 1000} KB, {DiffLimiter.MaxFileChars / 1000} KB per file).";
    }

    /// <summary>
    /// Conferma (una volta per sessione, e di nuovo per ogni file dal nome sospetto non ancora confermato) di inviare
    /// il diff dei soli file selezionati ai provider elencati. Senza dialogo (test) non chiede nulla.
    /// </summary>
    private async Task<bool> ConfirmDisclosureAsync(IReadOnlyList<string> paths, string? chain, CancellationToken ct)
    {
        if (_dialogs is null)
            return true;

        var sensitive = SensitiveFiles.Find(paths).Where(p => !_confirmedSensitive.Contains(p)).ToList();
        if (_disclosureAccepted && sensitive.Count == 0)
            return true;

        var message = $"Per usare l'AI, il diff dei soli file selezionati ({paths.Count} elementi) verrà inviato a un servizio esterno: " +
                      $"{chain ?? "provider AI configurato"}.";
        if (chain is not null && chain.Contains("→"))
            message += " I provider vengono provati nell'ordine indicato: se il primo fallisce, il diff passa al successivo.";
        message += " Nessun altro file viene inviato. Questa conferma compare una volta per sessione.";
        if (sensitive.Count > 0)
            message += "\n\nATTENZIONE: questi file sembrano contenere segreti (chiavi, certificati, configurazioni):\n" +
                       string.Join("\n", sensitive.Take(8).Select(p => $"• {Path.GetFileName(p)}")) +
                       (sensitive.Count > 8 ? $"\n… e altri {sensitive.Count - 8}" : string.Empty) +
                       "\nDeselezionali se non vuoi che il loro contenuto lasci il computer.";
        message += "\n\nProcedere?";

        if (!await _dialogs.ConfirmAsync(message, ct))
            return false;

        _disclosureAccepted = true;
        _confirmedSensitive.UnionWith(sensitive);
        return true;
    }

    /// <summary>Elenco dei provider che possono ricevere il diff, in ordine ("A → B").</summary>
    internal static string? DescribeChain(IAiService service) => service switch
    {
        FallbackAiService fallback => string.Join(" → ", fallback.ProviderNames),
        _ => DescribeProvider(service),
    };

    /// <summary>Nome del provider che ha risposto, se ricavabile a costo zero.</summary>
    internal static string? DescribeProvider(IAiService service) => service switch
    {
        FallbackAiService fallback => fallback.LastUsedProvider,
        CopilotAiService => "GitHub Copilot",
        ClaudeCodeAiService => "Claude Code",
        AnthropicAiService => "Claude API",
        AiService => "OpenAI-compatibile",
        _ => null,
    };
}
