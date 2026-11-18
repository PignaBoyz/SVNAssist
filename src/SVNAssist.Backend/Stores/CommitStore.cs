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
        set { lock (_lock) { _aiStatus = value; _aiAction = null; _aiSeq++; } Changed?.Invoke(); }
    }

    private StatusActionDto? _aiAction;
    private string? _aiProviderHint;

    /// <summary>Provider (<c>copilot</c>, <c>claude</c>...) dell'ultimo errore legato al modello: la procedura "Aggiorna elenco modelli" parte da li'. Null = non noto.</summary>
    internal string? FailedProvider { get; private set; }

    /// <summary>
    /// Mostra l'errore vero del provider. Se la causa e' il modello scelto (ritirato, non abilitato, rinominato: succede con Copilot)
    /// il messaggio lo dice chiaramente e compare il pulsante "Aggiorna elenco modelli" per sceglierne un altro.
    /// </summary>
    internal void SetAiError(string detail)
    {
        var kind = AiErrors.Classify(detail);
        if (kind == AiErrorKind.Other)
        {
            AiStatus = Loc.T("Errore AI: {0}", detail);
            return;
        }

        FailedProvider = AiErrors.ProviderOf(detail) ?? _aiProviderHint;
        if (kind == AiErrorKind.Auth)
        {
            lock (_lock)
            {
                _aiStatus = Loc.T("Errore AI: accesso al provider scaduto o non valido. Rifai l'accesso e premi di nuovo \"Genera con AI\".\nDettaglio: {0}", detail);
                _aiAction = new StatusActionDto(Loc.T("Rifai l'accesso"), "wizardOpenAuth");
                _aiSeq++;
            }

            Changed?.Invoke();
            return;
        }

        var msg = kind == AiErrorKind.ModelUnavailable
            ? Loc.T("Errore AI: il modello scelto non è più disponibile presso il provider (ritirato, rinominato o non abilitato per il tuo account). Aggiorna l'elenco dei modelli e scegline un altro.\nDettaglio: {0}", detail)
            : Loc.T("Errore AI: {0}\nPotrebbe dipendere dal modello scelto: aggiorna l'elenco dei modelli e provane un altro.", detail);
        lock (_lock)
        {
            _aiStatus = msg;
            _aiAction = new StatusActionDto(Loc.T("Aggiorna elenco modelli"), "wizardOpenModels");
            _aiSeq++;
        }

        Changed?.Invoke();
    }

    private int _aiSeq;

    /// <summary>Gravita' del messaggio per la UI (colore/icona): i messaggi "mancano i presupposti" sono avvisi, "Errore..." e' un errore.</summary>
    internal static string LevelOf(string status, bool busy)
    {
        if (busy)
            return "busy";
        if (status.Length == 0)
            return string.Empty;
        // Prefissi italiani e inglesi (i testi sono nella lingua corrente al momento in cui sono stati scritti).
        if (status.StartsWith("Error", StringComparison.Ordinal))
            return "err";
        if (StartsWithAny(status, "Nessun", "Scrivi prima", "Operazione", "No ", "First write", "Operation"))
            return "warn";
        return StartsWithAny(status, "Messaggio", "Message") ? "ok" : "info";
    }

    private static bool StartsWithAny(string text, params string[] prefixes)
        => prefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal));

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
    internal CommitDto GetSnapshot(DownloadDto? download, int count = 0, bool all = false)
    {
        lock (_lock)
            return new CommitDto(_text, _aiStatus, LevelOf(_aiStatus, _busy), _aiSeq, _busy, _lang, _trace, download, count, all, AiAction: _aiAction);
    }

    /// <summary>Cambia la lingua del messaggio AI (valori: auto, italiano, english).</summary>
    internal void SetLanguage(string lang)
    {
        var value = lang?.Trim().ToLowerInvariant();
        if (value is not ("auto" or "italiano" or "english"))
            throw new ArgumentException(Loc.T("Lingua non valida: {0}", lang));

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

    /// <summary>Se impostato, "Genera con AI" senza provider utilizzabile apre la procedura guidata invece del prompt <see cref="IAiSetupPrompt"/>.</summary>
    internal Func<CancellationToken, Task>? OpenAiWizard { get; set; }

    internal Task GenerateAsync(CancellationToken ct) => RunAiAsync(improve: false, ct);

    internal Task ImproveAsync(CancellationToken ct, string? style = null) => RunAiAsync(improve: true, ct, style);

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
    private async Task RunAiAsync(bool improve, CancellationToken ct, string? style = null)
    {
        // Un solo flusso AI alla volta: il doppio click non avvia due richieste ne' due guide di configurazione.
        if (Interlocked.CompareExchange(ref _aiRunning, 1, 0) != 0)
            return;

        try
        {
            IsBusy = true;
            AiStatus = improve ? Loc.T("Miglioramento in corso...") : Loc.T("Generazione in corso..."); // riscontro immediato a ogni clic
            await RunAiCoreAsync(improve, ct, style);
        }
        catch (OperationCanceledException)
        {
            // Mai silenzio: anche un annullamento deve lasciare un feedback.
            AiStatus = ct.IsCancellationRequested ? Loc.T("Operazione AI annullata.") : Loc.T("Errore AI: richiesta interrotta per timeout.");
        }
        catch (Exception ex)
        {
            SetAiError(ex.Message);
        }
        finally
        {
            IsBusy = false;
            Volatile.Write(ref _aiRunning, 0);
        }
    }

    private async Task RunAiCoreAsync(bool improve, CancellationToken ct, string? style = null)
    {
        SetTrace(null, null, finished: true);
        var ai = await _aiServiceFactory.CreateAsync(ct);
        if (ai is null && OpenAiWizard is { } openWizard)
        {
            AiStatus = Loc.T("Nessun provider AI configurato: completa la procedura guidata.");
            await openWizard(ct);
            return;
        }

        if (ai is null)
        {
            AiStatus = Loc.T("Nessun provider AI disponibile (Copilot, Claude o token GitHub).");
            // La guida compare a ogni click: se l'utente la chiude senza fare nulla riceve comunque questo messaggio.
            AiStatus += await _aiSetupPrompt.ShowMissingTokenHelpAsync(ct)
                ? Loc.T(" Completato il login o creato il token, premi di nuovo \"Genera con AI\".")
                : Loc.T(" Premi di nuovo \"Genera con AI\" per rivedere come configurarlo.");
            return;
        }

        if (improve && string.IsNullOrWhiteSpace(Text))
        {
            AiStatus = Loc.T("Scrivi prima una bozza del messaggio da migliorare.");
            return;
        }

        var workingCopy = _workingCopyPath();
        if (string.IsNullOrWhiteSpace(workingCopy))
        {
            AiStatus = Loc.T("Nessuna working copy configurata.");
            return;
        }

        // L'AI descrive gli stessi file del commit: quelli in Staged, o tutte le modifiche versionate se Staged e' vuoto.
        var paths = _stagedPaths();
        if (paths.Count == 0)
        {
            AiStatus = Loc.T("Nessuna modifica da committare.");
            return;
        }

        _aiProviderHint = ProviderIdOf(ai);
        var chain = DescribeChain(ai);
        if (!await ConfirmDisclosureAsync(paths, chain, ct))
        {
            AiStatus = Loc.T("Operazione annullata: nessun dato è stato inviato.");
            return;
        }

        var chainNames = ai is FallbackAiService fb ? fb.ProviderNames.ToList() : DescribeProvider(ai) is { } single ? [single] : null;
        SetTrace(chainNames, null, finished: false);
        AiStatus = chain is null
            ? (improve ? Loc.T("Miglioramento in corso...") : Loc.T("Generazione in corso..."))
            : (improve ? Loc.T("Miglioramento con {0}...", chain) : Loc.T("Generazione con {0}...", chain));

        var diff = await _svnService.GetDiffForAiAsync(paths, workingCopy, ct);
        if (!improve && string.IsNullOrWhiteSpace(diff))
        {
            AiStatus = Loc.T("Nessun diff disponibile.");
            SetTrace(null, null, finished: true);
            return;
        }

        // Nessun troncamento: i diff grandi vengono analizzati a blocchi (map-reduce) dal servizio AI, con avanzamento nello stato.
        if (ai is AiServiceBase directAi) directAi.Progress = m => AiStatus = m;
        else if (ai is FallbackAiService chainAi) chainAi.Progress = m => AiStatus = m;

        await InitializeAsync(ct);
        var language = Language;
        var rules = CommitInstructions.Load(workingCopy);
        string result;
        try
        {
            result = improve
                ? await ai.ImproveCommitMessageAsync(Text, diff, language, ct, rules, style)
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
        var done = improve ? Loc.T("Messaggio migliorato") : Loc.T("Messaggio generato con AI");
        var usedModel = ai switch { FallbackAiService f => f.LastUsedModel, IReportsModel r => r.LastModelUsed, _ => null };
        var showUsage = await _settings.GetAiShowUsageAsync(ct);
        var last = ai is FallbackAiService fl ? fl.LastService : ai;
        var isCopilot = last is CopilotAiService;
        if (!showUsage && last is ClaudeCodeAiService or AnthropicAiService or AiService)
            usedModel = null; // il modello configurato (non quello che il provider dichiara) si scrive solo con l'opzione
        var detail = string.Join(", ", new[]
        {
            provider,
            usedModel is null ? null : Loc.T("modello {0}", usedModel),
            showUsage ? await UsageCostAsync(isCopilot, usedModel, ct) : null,
        }.Where(x => x is not null));
        AiStatus = detail.Length == 0 ? $"{done}." : $"{done} ({detail}).";
        if (rules is not null)
            AiStatus += Loc.T(" Regole di commit del progetto applicate.");
    }

    /// <summary>Elenco modelli da cui leggere il costo dichiarato dal provider (null = nessun costo disponibile).</summary>
    internal IAiModelCatalog? ModelCatalog { get; set; }

    /// <summary>
    /// Costo dichiarato dal provider per il modello usato (oggi solo Copilot: richieste premium dall'elenco modelli). Mai stimato: se manca,
    /// e' strano (vuoto, a capo, troppo lungo) o l'elenco non risponde, si scrive che non e' disponibile.
    /// </summary>
    private async Task<string> UsageCostAsync(bool isCopilot, string? model, CancellationToken ct)
    {
        var cost = string.Empty;
        if (isCopilot && model is not null && ModelCatalog is not null)
        {
            try
            {
                var list = await ModelCatalog.GetAsync("copilot", false, ct);
                cost = list.Models.FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase))?.Cost?.Trim() ?? string.Empty;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                BackendLog.Warn("Costo del modello non disponibile: " + ex.Message);
            }
        }

        return cost.Length is > 0 and <= 80 && !cost.Contains('\n') ? Loc.T("costo: {0}", cost) : Loc.T("costo non disponibile");
    }

    /// <summary>
    /// Conferma (una volta per sessione, e di nuovo per ogni file dal nome sospetto non ancora confermato) di inviare
    /// il diff dei file da committare ai provider elencati. Senza dialogo (test) non chiede nulla.
    /// </summary>
    private async Task<bool> ConfirmDisclosureAsync(IReadOnlyList<string> paths, string? chain, CancellationToken ct)
    {
        if (_dialogs is null)
            return true;

        var sensitive = SensitiveFiles.Find(paths).Where(p => !_confirmedSensitive.Contains(p)).ToList();
        if (_disclosureAccepted && sensitive.Count == 0)
            return true;

        var message = Loc.T("Per usare l'AI, il diff dei file da committare ({0} elementi) verrà inviato a un servizio esterno: {1}.",
            paths.Count, chain ?? Loc.T("provider AI configurato"));
        if (chain is not null && chain.Contains("→"))
            message += Loc.T(" I provider vengono provati nell'ordine indicato: se il primo fallisce, il diff passa al successivo.");
        message += Loc.T(" Nessun altro file viene inviato. Questa conferma compare una volta per sessione.");
        if (sensitive.Count > 0)
            message += "\n\n" + Loc.T("ATTENZIONE: questi file sembrano contenere segreti (chiavi, certificati, configurazioni):") + "\n" +
                       string.Join("\n", sensitive.Take(8).Select(p => $"• {Path.GetFileName(p)}")) +
                       (sensitive.Count > 8 ? "\n" + Loc.T("… e altri {0}", sensitive.Count - 8) : string.Empty) +
                       "\n" + Loc.T("Metti in Staged solo i file il cui contenuto può lasciare il computer.");
        message += "\n\n" + Loc.T("Procedere?");

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

    /// <summary>Id del provider (<c>copilot</c>, <c>claudecode</c>, <c>claude</c>, <c>openai</c>) di un servizio singolo; null per la catena di fallback.</summary>
    internal static string? ProviderIdOf(IAiService service) => service switch
    {
        CopilotAiService => "copilot",
        ClaudeCodeAiService => "claudecode",
        AnthropicAiService => "claude",
        AiService => "openai",
        _ => null,
    };

    /// <summary>Nome del provider che ha risposto, se ricavabile a costo zero.</summary>
    internal static string? DescribeProvider(IAiService service) => service switch
    {
        FallbackAiService fallback => fallback.LastUsedProvider,
        CopilotAiService => "GitHub Copilot",
        ClaudeCodeAiService => "Claude Code",
        AnthropicAiService => "Claude API",
        AiService => Loc.T("OpenAI-compatibile"),
        _ => null,
    };
}
