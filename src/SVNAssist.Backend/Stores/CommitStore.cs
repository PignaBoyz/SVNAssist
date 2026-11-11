using System.IO;
using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>
/// Messaggio di commit con le azioni AI (Genera / Migliora) e la scelta della lingua nel pannello.
/// </summary>
/// <remarks>
/// Non conosce la lista file: riceve da chi lo crea le funzioni che restituiscono i percorsi
/// in Staged e la working copy, cosi' resta testabile da solo.
/// </remarks>
internal sealed class CommitStore
{
    private readonly ISvnService _svnService;
    private readonly IAiServiceFactory _aiServiceFactory;
    private readonly IAiSetupPrompt _aiSetupPrompt;
    private readonly ISvnAssistSettings _settings;
    private readonly Func<IReadOnlyList<string>> _stagedPaths;
    private readonly Func<string> _workingCopyPath;
    private readonly IDialogService? _dialogs;
    private readonly object _lock = new();

    // Trasparenza: prima del primo invio al provider AI (e per ogni file sospetto nuovo) l'utente conferma.
    private bool _disclosureAccepted;
    private readonly HashSet<string> _confirmedSensitive = new(StringComparer.OrdinalIgnoreCase);

    private string _text = string.Empty;
    private string _aiStatus = string.Empty;
    private bool _busy;
    private int _aiRunning;
    private string _lang = "auto";
    private bool _languageChosenByUser;
    private bool _languageLoaded;
    private IReadOnlyList<TraceDto> _trace = [];

    public CommitStore(
        ISvnAssistSettings settings,
        ISvnService svnService,
        IAiServiceFactory aiServiceFactory,
        IAiSetupPrompt aiSetupPrompt,
        Func<IReadOnlyList<string>> stagedPaths,
        Func<string> workingCopyPath,
        IDialogService? dialogs = null)
    {
        _dialogs = dialogs;
        _settings = settings;
        _svnService = svnService;
        _aiServiceFactory = aiServiceFactory;
        _aiSetupPrompt = aiSetupPrompt;
        _stagedPaths = stagedPaths;
        _workingCopyPath = workingCopyPath;
    }

    /// <summary>Qualcosa e' cambiato (per rimandare lo snapshot).</summary>
    public event Action? Changed;

    /// <summary>Messaggio corrente (scritto dall'utente o generato dall'AI).</summary>
    internal string Text
    {
        get { lock (_lock) return _text; }
        set { lock (_lock) _text = value ?? string.Empty; Changed?.Invoke(); }
    }

    /// <summary>Aggiorna il testo senza notificare: lo ha appena digitato la UI, che lo tiene lei (niente eco).</summary>
    internal void SetTextQuietly(string text)
    {
        lock (_lock)
            _text = text ?? string.Empty;
    }

    /// <summary>Ultimo stato dell'AI (es. "Generazione in corso...", "Messaggio generato con Claude Code.").</summary>
    internal string AiStatus
    {
        get { lock (_lock) return _aiStatus; }
        set { lock (_lock) _aiStatus = value; Changed?.Invoke(); }
    }

    /// <summary>Un'operazione AI e' in corso.</summary>
    internal bool IsBusy
    {
        get { lock (_lock) return _busy; }
        private set { lock (_lock) _busy = value; Changed?.Invoke(); }
    }

    /// <summary>Lingua scelta ("auto" / "italiano" / "english"). Vale per la sessione: non viene riscritta nelle impostazioni.</summary>
    internal string Language
    {
        get { lock (_lock) return _lang; }
    }

    /// <summary>Parte dello snapshot <c>commit</c>.</summary>
    internal CommitDto GetSnapshot(DownloadDto? download)
    {
        lock (_lock)
            return new CommitDto(_text, _aiStatus, _busy, _lang, _trace, download);
    }

    /// <summary>Cambia la lingua del messaggio AI (valori: auto, italiano, english).</summary>
    internal void SetLanguage(string lang)
    {
        var value = lang?.Trim().ToLowerInvariant();
        if (value is not ("auto" or "italiano" or "english"))
            throw new ArgumentException($"Lingua non valida: {lang}");

        lock (_lock)
        {
            _languageChosenByUser = true;
            _lang = value;
        }

        Changed?.Invoke();
    }

    /// <summary>Legge la lingua iniziale dalle impostazioni (se l'utente non ha gia' scelto).</summary>
    internal async Task InitializeAsync(CancellationToken ct)
    {
        if (_languageLoaded)
            return;

        _languageLoaded = true;
        var stored = (await _settings.GetDefaultLanguageAsync(ct)).Trim().ToLowerInvariant();
        lock (_lock)
        {
            if (_languageChosenByUser)
                return;

            _lang = stored is "italiano" or "english" ? stored : "auto";
        }

        Changed?.Invoke();
    }

    internal Task GenerateAsync(CancellationToken ct) => RunAiAsync(improve: false, ct);

    internal Task ImproveAsync(CancellationToken ct) => RunAiAsync(improve: true, ct);

    /// <summary>Azzera messaggio e stato AI (dopo un commit riuscito).</summary>
    internal void Clear()
    {
        lock (_lock)
        {
            _text = string.Empty;
            _aiStatus = string.Empty;
            _trace = [];
        }

        Changed?.Invoke();
    }

    private void SetTrace(IReadOnlyList<string>? names, string? used, bool finished)
    {
        lock (_lock)
        {
            if (names is null || names.Count == 0)
            {
                _trace = [];
            }
            else if (!finished)
            {
                _trace = names.Select((n, i) => new TraceDto(n, i == 0 ? "run" : "wait", string.Empty)).ToList();
            }
            else
            {
                var usedIndex = used is null ? -1 : names.ToList().IndexOf(used);
                _trace = names.Select((n, i) => new TraceDto(n,
                    usedIndex < 0 ? "fail" : i < usedIndex ? "fail" : i == usedIndex ? "ok" : "skip", string.Empty)).ToList();
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Flusso comune a Genera e Migliora: provider, file in Staged, diff minimo, chiamata AI.</summary>
    private async Task RunAiAsync(bool improve, CancellationToken ct)
    {
        // Un solo flusso AI alla volta: il doppio click non avvia due richieste ne' due guide di configurazione.
        if (Interlocked.CompareExchange(ref _aiRunning, 1, 0) != 0)
            return;

        try
        {
            IsBusy = true;
            await RunAiCoreAsync(improve, ct);
        }
        catch (OperationCanceledException)
        {
            // Mai silenzio: anche un annullamento deve lasciare un feedback.
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
        SetTrace(null, null, finished: true);
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

        // L'AI descrive solo i file in Staged (gli stessi che finiranno nel commit).
        var paths = _stagedPaths();
        if (paths.Count == 0)
        {
            AiStatus = improve
                ? "Metti almeno un file in Staged per migliorare il messaggio."
                : "Metti almeno un file in Staged per generare il messaggio.";
            return;
        }

        var chain = DescribeChain(ai);
        if (!await ConfirmDisclosureAsync(paths, chain, ct))
        {
            AiStatus = "Operazione annullata: nessun dato è stato inviato.";
            return;
        }

        var chainNames = ai is FallbackAiService fb ? fb.ProviderNames.ToList() : DescribeProvider(ai) is { } single ? [single] : null;
        SetTrace(chainNames, null, finished: false);
        AiStatus = (improve ? "Miglioramento" : "Generazione") + (chain is null ? " in corso..." : $" con {chain}...");

        var diff = await _svnService.GetDiffForAiAsync(paths, workingCopy, ct);
        if (!improve && string.IsNullOrWhiteSpace(diff))
        {
            AiStatus = "Nessun diff disponibile.";
            SetTrace(null, null, finished: true);
            return;
        }

        // Tetto ai dati inviati: un file enorme in Staged per errore non deve diventare centinaia di KB e decine di richieste.
        var limited = DiffLimiter.Limit(diff);
        diff = limited.Diff;

        await InitializeAsync(ct);
        var language = Language;
        var rules = CommitInstructions.Load(workingCopy);
        string result;
        try
        {
            result = improve
                ? await ai.ImproveCommitMessageAsync(Text, diff, language, ct, rules)
                : await ai.GenerateCommitMessageAsync(diff, language, ct, rules);
        }
        catch
        {
            SetTrace(chainNames, null, finished: true);
            throw;
        }

        Text = result;
        var provider = DescribeProvider(ai);
        SetTrace(chainNames, provider, finished: true);
        var done = improve ? "Messaggio migliorato" : "Messaggio generato con AI";
        AiStatus = provider is null ? $"{done}." : $"{done} ({provider}).";
        if (rules is not null)
            AiStatus += " Regole di commit del progetto applicate.";
        if (limited.WasTruncated)
            AiStatus += $" Attenzione: diff molto grande, inviata solo una parte (max {DiffLimiter.MaxTotalChars / 1000} KB, {DiffLimiter.MaxFileChars / 1000} KB per file).";
    }

    /// <summary>
    /// Conferma (una volta per sessione, e di nuovo per ogni file dal nome sospetto non ancora confermato) di inviare
    /// il diff dei soli file in Staged ai provider elencati. Senza dialogo (test) non chiede nulla.
    /// </summary>
    private async Task<bool> ConfirmDisclosureAsync(IReadOnlyList<string> paths, string? chain, CancellationToken ct)
    {
        if (_dialogs is null)
            return true;

        var sensitive = SensitiveFiles.Find(paths).Where(p => !_confirmedSensitive.Contains(p)).ToList();
        if (_disclosureAccepted && sensitive.Count == 0)
            return true;

        var message = $"Per usare l'AI, il diff dei soli file in Staged ({paths.Count} elementi) verrà inviato a un servizio esterno: " +
                      $"{chain ?? "provider AI configurato"}.";
        if (chain is not null && chain.Contains("→"))
            message += " I provider vengono provati nell'ordine indicato: se il primo fallisce, il diff passa al successivo.";
        message += " Nessun altro file viene inviato. Questa conferma compare una volta per sessione.";
        if (sensitive.Count > 0)
            message += "\n\nATTENZIONE: questi file sembrano contenere segreti (chiavi, certificati, configurazioni):\n" +
                       string.Join("\n", sensitive.Take(8).Select(p => $"• {Path.GetFileName(p)}")) +
                       (sensitive.Count > 8 ? $"\n… e altri {sensitive.Count - 8}" : string.Empty) +
                       "\nTogli dallo Staged i file di cui non vuoi che il contenuto lasci il computer.";
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
