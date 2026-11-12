using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>
/// Procedura guidata per configurare il provider AI dei commit message (protocollo: comandi <c>wizard*</c>).
/// Passi: 1 provider, 2 prerequisiti (installazioni), 3 accesso (login / chiave), 4 modello.
/// I controlli e le azioni sono quelli di <see cref="PrereqStore"/>/<see cref="PrerequisitesService"/> (filtrati per il provider scelto);
/// il salvataggio usa le stesse chiavi di Tools > Options. La UI mostra soltanto lo stato.
/// </summary>
internal sealed class WizardStore
{
    internal const string AuthGitHub = "github", AuthClaudeCode = "claudecode", AuthKey = "key", AuthOpenAi = "openai";

    internal static readonly string[] ProviderIds = ["copilot", "claudecode", "claude", "openai"];

    private static readonly IReadOnlyList<WizardProviderDto> Providers =
    [
        new("copilot", "GitHub Copilot", AiProviderGuide.Requirements("copilot")),
        new("claudecode", "Claude Code (abbonamento Claude)", AiProviderGuide.Requirements("claudecode")),
        new("claude", "Claude API (API key, a consumo)", AiProviderGuide.Requirements("claude")),
        new("openai", "GitHub Models / OpenAI-compatibile", AiProviderGuide.Requirements("openai")),
    ];

    // Etichette come nelle tendine di Tools > Options (OptionsPage.cs): tenere allineate; il primo e' il consigliato.
    private static readonly WizardModelDto[] ClaudeModels =
        [new("claude-sonnet-5-5", "Sonnet (consigliato, equilibrato)"), new("claude-haiku-5-5", "Haiku (il più economico e veloce)"), new("claude-opus-5-5", "Opus (il più potente, costoso)")];
    private static readonly WizardModelDto[] CopilotModels =
        [new("default", "Predefinito di Copilot (consigliato)"), new("gpt-4.1", "GPT-4.1"), new("gpt-5-mini", "GPT-5 mini"), new("claude-sonnet-4.5", "Claude Sonnet 4.5")];
    private static readonly WizardModelDto[] OpenAiModels =
        [new("openai/gpt-4o-mini", "GPT-4o mini (consigliato, gratuito su GitHub Models)"), new("openai/gpt-4o", "GPT-4o"), new("openai/gpt-4.1-mini", "GPT-4.1 mini"), new("openai/gpt-4.1", "GPT-4.1")];

    private readonly PrereqStore _prereq;
    private readonly ISvnAssistSettings _settings;
    private readonly object _lock = new();

    internal const string ModeAi = "ai", ModePrereq = "prereq", AiRowId = "ai";

    private bool _open, _fromGenerate, _busy, _authOk, _ghInstalled;
    private string _mode = ModeAi;
    private int _step = 1;
    private string _provider = string.Empty, _authDetail = string.Empty, _endpoint = string.Empty, _model = string.Empty;
    private IReadOnlyList<PrereqItemDto> _items = [];

    public WizardStore(PrereqStore prereq, ISvnAssistSettings settings)
    {
        _prereq = prereq;
        _settings = settings;
        // Il feedback delle azioni (es. "Si è aperto un terminale...") sta in PrereqStore: rimanda lo snapshot anche per quello.
        _prereq.Changed += () => { if (_open) Changed?.Invoke(); };
    }

    /// <summary>Qualcosa e' cambiato (per rimandare lo snapshot).</summary>
    public event Action? Changed;

    internal static IReadOnlyList<WizardModelDto> ModelsFor(string provider) => provider switch
    {
        "copilot" => CopilotModels,
        "claudecode" or "claude" => ClaudeModels,
        "openai" => OpenAiModels,
        _ => [],
    };

    /// <summary>Chiave dell'impostazione che contiene il modello del provider.</summary>
    internal static string ModelSettingKey(string provider) => provider switch
    {
        "copilot" => SettingDefinitions.CopilotModel,
        "openai" => SettingDefinitions.AiModel,
        _ => SettingDefinitions.ClaudeModel,
    };

    /// <summary>Prerequisiti da installare nel passo 2 (il login/la chiave sono del passo 3).</summary>
    private static string[] InstallIds(string provider) => provider switch
    {
        "copilot" => [PrerequisiteIds.GitHubCli, PrerequisiteIds.CopilotRuntime],
        "claudecode" => [PrerequisiteIds.ClaudeCode],
        _ => [],
    };

    private bool CanNextLocked() => !_busy && (_mode == ModePrereq ? _items.All(i => i.Ok) : _step switch
    {
        1 => _provider.Length > 0,
        2 => _items.All(i => i.Ok),
        3 => _authOk || _provider == "claudecode",
        _ => _model.Length > 0,
    });

    internal WizardDto GetSnapshot()
    {
        var feedback = _open ? _prereq.Feedback : string.Empty;
        lock (_lock)
        {
            var auth = _provider switch
            {
                "copilot" => AuthGitHub,
                "claudecode" => AuthClaudeCode,
                "claude" => AuthKey,
                "openai" => AuthOpenAi,
                _ => string.Empty,
            };
            return new WizardDto(_open, _mode, _step, _fromGenerate, _provider, _busy, feedback, Providers,
                _step == 2 || _mode == ModePrereq ? _items : [], auth, _authOk, _authDetail, _endpoint,
                _step == 4 ? ModelsFor(_provider) : [], _model, CanNextLocked());
        }
    }

    /// <summary>Apre la procedura dal passo 1, con il provider configurato (se non e' "auto") preselezionato.</summary>
    internal async Task OpenAsync(bool fromGenerate, CancellationToken ct)
    {
        var configured = AiProviderGuide.Normalize(await _settings.GetAiProviderAsync(ct));
        lock (_lock)
        {
            _open = true;
            _mode = ModeAi;
            _step = 1;
            _fromGenerate = fromGenerate;
            _provider = ProviderIds.Contains(configured) ? configured : string.Empty;
            _items = [];
            _model = string.Empty;
        }

        _prereq.Feedback = string.Empty;
        Changed?.Invoke();
    }

    /// <summary>Apre la procedura sui prerequisiti che mancano (barra in basso): svn e provider AI, con cosa fare per ciascuno.</summary>
    internal async Task OpenPrereqAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            _open = true;
            _mode = ModePrereq;
            _step = 1;
            _fromGenerate = false;
            _items = [];
        }

        _prereq.Feedback = string.Empty;
        await RefreshAsync(ct);
        Changed?.Invoke();
    }

    internal void Cancel()
    {
        lock (_lock)
            _open = false;
        Changed?.Invoke();
    }

    internal void Pick(string provider)
    {
        if (!ProviderIds.Contains(provider))
            throw new ArgumentException($"Provider sconosciuto: {provider}");
        lock (_lock)
        {
            if (_step != 1)
                return;
            _provider = provider;
        }

        Changed?.Invoke();
    }

    internal void Back()
    {
        lock (_lock)
        {
            if (_step > 1)
                _step--;
        }

        Changed?.Invoke();
    }

    /// <summary>Passo avanti, solo se il passo corrente e' completo (ricontrolla prima: lo stato puo' essere cambiato).</summary>
    internal async Task NextAsync(CancellationToken ct)
    {
        if (_step is 2 or 3)
            await RefreshAsync(ct);
        int step;
        lock (_lock)
        {
            if (!CanNextLocked() || _step >= 4)
                return;
            step = ++_step;
            if (step == 4)
                _model = ModelsFor(_provider)[0].Value;
        }

        Changed?.Invoke();
        if (step < 4)
            await RefreshAsync(ct);
    }

    /// <summary>Rilegge prerequisiti e accesso del provider scelto (senza toccare le impostazioni salvate).</summary>
    internal async Task RefreshAsync(CancellationToken ct)
    {
        string provider, mode;
        lock (_lock)
        {
            provider = _provider;
            mode = _mode;
        }

        if (mode == ModePrereq)
        {
            var all = await _prereq.Service.CheckAsync(ct);
            var rows = all.Items.Where(i => i.Id == PrerequisiteIds.Svn).Select(PrereqStore.ToDto).ToList();
            rows.Add(new PrereqItemDto(AiRowId, "Provider AI", "Provider AI per i commit message", all.HasAiProvider,
                all.HasAiProvider ? string.Join(" → ", all.UsableProviders) : "Nessun provider AI utilizzabile: serve per generare i messaggi di commit.",
                all.HasAiProvider ? string.Empty : "La procedura guidata ti accompagna nella scelta e nell'accesso.",
                all.HasAiProvider ? string.Empty : "Configura l'AI", string.Empty));
            lock (_lock)
            {
                if (_mode != ModePrereq)
                    return;
                _items = rows;
            }

            Changed?.Invoke();
            return;
        }

        if (provider.Length == 0)
            return;

        var snapshot = await _prereq.Service.CheckAsync(ct, provider);
        var endpoint = await _settings.GetAiEndpointAsync(ct);
        string? authId = provider switch
        {
            "copilot" => PrerequisiteIds.GitHubLogin,
            "claude" => PrerequisiteIds.AnthropicKey,
            "openai" => PrerequisiteIds.OpenAiKey,
            _ => null,
        };
        var install = InstallIds(provider);
        var auth = snapshot.Items.FirstOrDefault(i => i.Id == authId);

        lock (_lock)
        {
            if (_provider != provider)
                return; // l'utente ha cambiato idea mentre si controllava
            _items = snapshot.Items.Where(i => install.Contains(i.Id)).Select(PrereqStore.ToDto).ToList();
            _ghInstalled = snapshot.Items.Any(i => i.Id == PrerequisiteIds.GitHubCli && i.IsOk);
            _authOk = auth?.IsOk ?? false;
            _authDetail = provider == "claudecode"
                ? "Il login a Claude Code non si può verificare da qui: se non l'hai mai fatto, aprilo e digita /login."
                : auth?.Detail ?? string.Empty;
            _endpoint = endpoint;
        }

        Changed?.Invoke();
    }

    /// <summary>Azione di un prerequisito del passo 2 (stessa logica della checklist), poi ricontrolla.</summary>
    internal async Task RunAsync(string id, bool primary, CancellationToken ct)
    {
        IReadOnlyList<PrereqItemDto> rows;
        lock (_lock)
        {
            rows = _items;
            _busy = true;
        }

        Changed?.Invoke();
        try
        {
            if (id == AiRowId && rows.Any(r => r.Id == AiRowId))
            {
                lock (_lock)
                    _busy = false;
                await OpenAsync(false, ct); // dai prerequisiti alla procedura dell'AI
                return;
            }

            await _prereq.RunActionAsync(id, primary, ct, rows);
            await RefreshAsync(ct);
        }
        finally
        {
            lock (_lock)
                _busy = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Passo 3: apre nel terminale il login (GitHub o Claude Code; se manca gh lo installa prima).</summary>
    internal async Task LoginAsync(CancellationToken ct)
    {
        string provider;
        bool gh;
        lock (_lock)
        {
            provider = _provider;
            gh = _ghInstalled;
        }

        if (provider == "claudecode")
            await _prereq.LaunchAsync(ConsoleCommand.ClaudeLogin, "Si è aperto un terminale con Claude Code: se richiesto esegui /login, poi torna qui.");
        else if (gh)
            await _prereq.LaunchAsync(ConsoleCommand.GitHubLogin, "Si è aperto un terminale: conferma il codice nel browser, poi torna qui.");
        else
            await _prereq.LaunchAsync(ConsoleCommand.InstallGitHubCli, "Si è aperto un terminale con l'installazione di GitHub CLI: al termine premi di nuovo Accedi (se gh non viene trovato, riavvia VS).");
    }

    /// <summary>Passo 3: salva la chiave (Claude API) o endpoint e chiave (OpenAI-compatibile). Valori vuoti = non cambiare.</summary>
    internal async Task SetAuthAsync(string endpoint, string key, CancellationToken ct)
    {
        string provider;
        lock (_lock)
            provider = _provider;

        endpoint = endpoint.Trim();
        key = key.Trim();
        if (provider == "openai" && endpoint.Length > 0)
        {
            // L'endpoint riceve la chiave: solo https (o http verso la macchina locale, es. Ollama).
            var ok = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
            if (!ok)
                throw new ArgumentException("L'endpoint deve essere un URL https (o http://localhost).");
            await _settings.SetSettingAsync(SettingDefinitions.AiEndpoint, endpoint, ct);
        }

        if (key.Length > 0 && provider is "claude" or "openai")
            await _settings.SetSettingAsync(provider == "claude" ? SettingDefinitions.AnthropicApiKey : SettingDefinitions.AiApiKey, key, ct);

        await RefreshAsync(ct);
    }

    /// <summary>
    /// Passo 4: salva provider e modello (come Tools > Options) e chiude. <c>true</c> se la procedura era partita da
    /// "Genera con AI" (il chiamante lancia la generazione).
    /// </summary>
    internal async Task<bool> FinishAsync(string model, CancellationToken ct)
    {
        string provider;
        bool from;
        lock (_lock)
        {
            if (_step != 4)
                throw new InvalidOperationException("La procedura non è arrivata al passo del modello.");
            provider = _provider;
            from = _fromGenerate;
        }

        if (!ModelsFor(provider).Any(m => m.Value == model))
            throw new ArgumentException($"Modello non valido: {model}");

        await _settings.SetSettingAsync(SettingDefinitions.AiProvider, provider, ct);
        await _settings.SetSettingAsync(ModelSettingKey(provider), model, ct);
        lock (_lock)
        {
            _open = false;
            _model = model;
        }

        Changed?.Invoke();
        await _prereq.RefreshAsync(ct);
        return from;
    }
}
