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
    internal const string AuthGitHub = "github", AuthClaudeCode = "claudecode", AuthKey = "key", AuthOpenAi = "openai", AuthGemini = "gemini";

    internal static readonly string[] ProviderIds = ["copilot", "claudecode", "claude", "openai", "gemini"];

    private static IReadOnlyList<WizardProviderDto> Providers =>
    [
        new("copilot", "GitHub Copilot", AiProviderGuide.Requirements("copilot")),
        new("claudecode", Loc.T("Claude Code (abbonamento Claude)"), AiProviderGuide.Requirements("claudecode")),
        new("claude", Loc.T("Claude API (API key, a consumo)"), AiProviderGuide.Requirements("claude")),
        new("openai", Loc.T("GitHub Models / OpenAI-compatibile"), AiProviderGuide.Requirements("openai")),
        new("gemini", Loc.T("Gemini (API key Google)"), AiProviderGuide.Requirements("gemini")),
    ];

    private readonly PrereqStore _prereq;
    private readonly ISvnAssistSettings _settings;
    private readonly IAiModelCatalog _catalog;
    private readonly object _lock = new();

    /// <summary>Quanto e' vecchia al massimo la copia dell'elenco modelli quando si apre il passo "Modello" (24 ore solo in background: qui conta avere l'elenco fresco).</summary>
    internal static readonly TimeSpan WizardModelsMaxAge = TimeSpan.FromHours(1);

    internal const string ModeAi = "ai", ModePrereq = "prereq", AiRowId = "ai";

    private bool _open, _fromGenerate, _busy, _authOk, _ghInstalled;
    private string _mode = ModeAi;
    private int _step = 1;
    private string _provider = string.Empty, _authDetail = string.Empty, _endpoint = string.Empty, _model = string.Empty;
    // Passo "Modello": elenco dal provider (AiModelCatalog), con data, account e note. _modelsGen scarta le risposte di una richiesta superata.
    private IReadOnlyList<WizardModelDto> _models = [];
    private bool _modelsLoading, _modelsError;
    private string _modelsUpdated = string.Empty, _modelsNote = string.Empty, _modelsAccount = string.Empty, _modelsSource = string.Empty, _modelSaved = string.Empty;
    private int _modelsGen;
    private IReadOnlyList<PrereqItemDto> _items = [];

    public WizardStore(PrereqStore prereq, ISvnAssistSettings settings, IAiModelCatalog? catalog = null)
    {
        _prereq = prereq;
        _settings = settings;
        _catalog = catalog ?? new AiModelCatalog();
        // Il feedback delle azioni (es. "Si è aperto un terminale...") sta in PrereqStore: rimanda lo snapshot anche per quello.
        _prereq.Changed += () => { if (_open) Changed?.Invoke(); };
    }

    /// <summary>Qualcosa e' cambiato (per rimandare lo snapshot).</summary>
    public event Action? Changed;

    /// <summary>Chiave dell'impostazione che contiene il modello del provider.</summary>
    internal static string ModelSettingKey(string provider) => provider switch
    {
        "copilot" => SettingDefinitions.CopilotModel,
        "openai" => SettingDefinitions.AiModel,
        "gemini" => SettingDefinitions.GeminiModel,
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
                "gemini" => AuthGemini,
                _ => string.Empty,
            };
            return new WizardDto(_open, _mode, _step, _fromGenerate, _provider, _busy, feedback, Providers,
                _step == 2 || _mode == ModePrereq ? _items : [], auth, _authOk, _authDetail, _endpoint,
                _step == 4 ? _models : [], _model, CanNextLocked(),
                _step == 4 && _modelsLoading, _step == 4 ? _modelsUpdated : string.Empty, _step == 4 ? _modelsNote : string.Empty, _step == 4 && _modelsError,
                _step == 4 ? _modelsAccount : string.Empty, _step == 4 ? _modelsSource : string.Empty, _step == 4 ? _modelSaved : string.Empty);
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
            ResetModelsLocked();
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
            ResetModelsLocked();
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
            throw new ArgumentException(Loc.T("Provider sconosciuto: {0}", provider));
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
            {
                ResetModelsLocked();
            }
        }

        Changed?.Invoke();
        if (step < 4)
            await RefreshAsync(ct);
        else
            await LoadModelsAsync(force: false, ct);
    }

    private void ResetModelsLocked()
    {
        _modelsGen++;
        _models = [];
        _modelsLoading = _modelsError = false;
        _modelsUpdated = _modelsNote = _modelsAccount = _modelsSource = _modelSaved = string.Empty;
    }

    /// <summary>
    /// Apre la procedura direttamente sul passo "Modello" (da "Aggiorna elenco modelli" dopo un errore del provider) con l'elenco riletto dal provider.
    /// <paramref name="provider"/> e' quello che ha fallito; se manca si usa quello configurato. Se non c'e' nessuno, parte dal passo 1.
    /// Con "Fine" la procedura rigenera il messaggio.
    /// </summary>
    internal async Task OpenModelsAsync(string? provider, CancellationToken ct)
    {
        var id = AiProviderGuide.Normalize(provider ?? string.Empty);
        if (!ProviderIds.Contains(id))
            id = AiProviderGuide.Normalize(await _settings.GetAiProviderAsync(ct));
        if (!ProviderIds.Contains(id))
        {
            await OpenAsync(fromGenerate: true, ct);
            return;
        }

        lock (_lock)
        {
            _open = true;
            _mode = ModeAi;
            _step = 4;
            _fromGenerate = true;
            _provider = id;
            _items = [];
            ResetModelsLocked();
        }

        _prereq.Feedback = string.Empty;
        Changed?.Invoke();
        await LoadModelsAsync(force: true, ct);
        await RefreshAsync(ct); // per "Indietro": stato di prerequisiti e accesso
    }

    /// <summary>
    /// Da "Rifai l'accesso" dopo un errore di autenticazione: apre la procedura sul passo Accesso del provider che ha fallito e, per i provider
    /// con login (Copilot, Claude Code), apre subito il terminale con il login. Con una API key resta il campo per incollarne una nuova.
    /// </summary>
    internal async Task OpenAuthAsync(string? provider, CancellationToken ct)
    {
        var id = AiProviderGuide.Normalize(provider ?? string.Empty);
        if (!ProviderIds.Contains(id))
            id = AiProviderGuide.Normalize(await _settings.GetAiProviderAsync(ct));
        if (!ProviderIds.Contains(id))
        {
            await OpenAsync(fromGenerate: true, ct);
            return;
        }

        lock (_lock)
        {
            _open = true;
            _mode = ModeAi;
            _step = 3;
            _fromGenerate = true;
            _provider = id;
            _items = [];
            ResetModelsLocked();
        }

        _prereq.Feedback = string.Empty;
        Changed?.Invoke();
        await RefreshAsync(ct);
        if (id is "copilot" or "claudecode")
            await LoginAsync(ct);
    }

    /// <summary>Pulsante "Aggiorna elenco": chiede di nuovo i modelli al provider, ignorando la copia salvata.</summary>
    internal async Task RefreshModelsAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_open || _step != 4 || _mode != ModeAi)
                return;
        }

        await LoadModelsAsync(force: true, ct);
    }

    /// <summary>Passo "Modello": prende l'elenco dal catalogo (provider), tiene la scelta corrente se esiste ancora e segnala il valore salvato non piu' in elenco.</summary>
    private async Task LoadModelsAsync(bool force, CancellationToken ct)
    {
        string provider;
        int gen;
        lock (_lock)
        {
            provider = _provider;
            gen = ++_modelsGen;
            _modelsLoading = true;
        }

        Changed?.Invoke();
        try
        {
            var list = await _catalog.GetAsync(provider, force, ct, WizardModelsMaxAge);
            var saved = await SavedModelAsync(provider, ct);

            var models = list.Models.Select(m => new WizardModelDto(m.Id, m.Label, m.Cost)).ToList();
            // "default" di Copilot (= lo sceglie Copilot) non e' mai "sparito": e' sempre valido anche se l'elenco non lo riporta.
            var missing = list.Models.Count > 0 && saved.Length > 0 && !(provider == "copilot" && saved == SettingDefinitions.CopilotDefaultModel)
                && !models.Any(m => string.Equals(m.Value, saved, StringComparison.OrdinalIgnoreCase));
            // Valore salvato non piu' in elenco: resta in uso finche' non si sceglie altro, ma lo si segnala (e lo si tiene selezionabile).
            if (missing)
                models.Add(new WizardModelDto(saved, Loc.T("{0} (non più nell'elenco del provider)", saved)));

            var notes = new List<string>();
            if (list.Error is not null)
                notes.Add(list.Source == "cache"
                    ? Loc.T("Elenco non aggiornato (mostro l'ultimo salvato): {0}", list.Error)
                    : Loc.T("Provider non collegato o non raggiungibile: collegalo e premi Aggiorna elenco. {0}", list.Error));
            else if (list.Source == "unlinked" && provider == "claudecode")
                notes.Add(Loc.T("Con Claude Code (abbonamento) il provider non permette di elencare i modelli: per vederli collega la API key Anthropic (provider Claude API). Usa \"Altro…\" per scrivere un id."));
            else if (list.Source == "unlinked")
                notes.Add(Loc.T("Provider non collegato: collegalo per vedere l'elenco dei modelli."));
            if (missing)
                notes.Add(Loc.T("Il modello salvato \"{0}\" non è più nell'elenco del provider: resta in uso finché non ne scegli un altro (potrebbe dare errore).", saved));

            lock (_lock)
            {
                if (gen != _modelsGen || _provider != provider)
                    return; // superata da una richiesta piu' recente o dal cambio provider
                var keep = _model;
                _models = models;
                _modelsSource = list.Source;
                _modelsUpdated = list.UpdatedAt?.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                _modelsAccount = list.Account ?? string.Empty;
                _modelsError = list.Error is not null;
                _modelsNote = string.Join("\n", notes);
                _modelSaved = saved;
                // Scelta: quella gia' fatta se esiste ancora; altrimenti il valore salvato se e' nell'elenco vero; altrimenti il primo.
                var listed = list.Models;
                _model = keep.Length > 0 && listed.Any(m => m.Id == keep) ? keep
                    : listed.FirstOrDefault(m => string.Equals(m.Id, saved, StringComparison.OrdinalIgnoreCase))?.Id
                      ?? (listed.Count > 0 ? listed[0].Id : saved);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                if (gen == _modelsGen)
                {
                    _modelsError = true;
                    _modelsNote = Loc.T("Provider non collegato o non raggiungibile: collegalo e premi Aggiorna elenco. {0}", ex.Message);
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                if (gen == _modelsGen)
                    _modelsLoading = false;
            }

            Changed?.Invoke();
        }
    }

    /// <summary>Modello oggi salvato per il provider, normalizzato come lo usa il servizio AI (Copilot vuoto = "default"). Vuoto = nessuno.</summary>
    private async Task<string> SavedModelAsync(string provider, CancellationToken ct)
    {
        switch (provider)
        {
            case "copilot":
                var c = await _settings.GetCopilotModelAsync(ct);
                return string.IsNullOrWhiteSpace(c) ? SettingDefinitions.CopilotDefaultModel : c.Trim();
            case "openai":
                return AiServiceFactory.NormalizeOpenAiModel(await _settings.GetAiEndpointAsync(ct), await _settings.GetAiModelAsync(ct));
            case "gemini":
                return AiServiceFactory.NormalizeGeminiModel(await _settings.GetGeminiModelAsync(ct));
            case "claude":
            case "claudecode":
                return AiServiceFactory.ResolveClaudeModel(await _settings.GetClaudeModelAsync(ct));
            default:
                return string.Empty;
        }
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
            rows.Add(new PrereqItemDto(AiRowId, Loc.T("Provider AI"), Loc.T("Provider AI per i commit message"), all.HasAiProvider,
                all.HasAiProvider ? string.Join(" → ", all.UsableProviders) : Loc.T("Nessun provider AI utilizzabile: serve per generare i messaggi di commit."),
                all.HasAiProvider ? string.Empty : Loc.T("La procedura guidata ti accompagna nella scelta e nell'accesso."),
                all.HasAiProvider ? string.Empty : Loc.T("Configura l'AI"), string.Empty));
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
            "gemini" => PrerequisiteIds.GeminiKey,
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
                ? Loc.T("Il login a Claude Code non si può verificare da qui: se non l'hai mai fatto, aprilo e digita /login.")
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
            await _prereq.LaunchAsync(ConsoleCommand.ClaudeLogin, Loc.T("Si è aperto un terminale con Claude Code: se richiesto esegui /login, poi torna qui."));
        else if (gh)
            await _prereq.LaunchAsync(ConsoleCommand.GitHubLogin, Loc.T("Si è aperto un terminale: conferma il codice nel browser, poi torna qui."));
        else
            await _prereq.LaunchAsync(ConsoleCommand.InstallGitHubCli, Loc.T("Si è aperto un terminale con l'installazione di GitHub CLI: al termine premi di nuovo Accedi (se gh non viene trovato, riavvia VS)."));
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
                throw new ArgumentException(Loc.T("L'endpoint deve essere un URL https (o http://localhost)."));
            await _settings.SetSettingAsync(SettingDefinitions.AiEndpoint, endpoint, ct);
        }

        if (key.Length > 0 && provider is "claude" or "openai" or "gemini")
            await _settings.SetSettingAsync(provider switch
            {
                "claude" => SettingDefinitions.AnthropicApiKey,
                "gemini" => SettingDefinitions.GeminiApiKey,
                _ => SettingDefinitions.AiApiKey,
            }, key, ct);

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
                throw new InvalidOperationException(Loc.T("La procedura non è arrivata al passo del modello."));
            provider = _provider;
            from = _fromGenerate;
        }

        // Qualsiasi id valido (dall'elenco del provider o scritto in "Altro..."): l'elenco non e' una lista chiusa, cosi' non si resta mai bloccati.
        model = (model ?? string.Empty).Trim();
        if (model.Length == 0)
            model = await SavedModelAsync(provider, ct);   // provider non collegato (nessun elenco): resta il modello in uso
        if (!AiModelIds.IsPlausible(model))
            throw new ArgumentException(Loc.T("Modello non valido: {0}", model));

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
