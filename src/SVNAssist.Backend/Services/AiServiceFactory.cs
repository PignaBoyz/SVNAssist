using System.Net.Http;

namespace SVNAssist.Services;

/// <summary>
/// Crea il servizio AI in base al provider scelto nelle impostazioni.
/// </summary>
/// <remarks>
/// Il ViewModel conosce solo <see cref="IAiServiceFactory"/>: non sa quali provider esistono
/// né come si configurano, ed è unit-testabile con una factory finta.
/// </remarks>
internal interface IAiServiceFactory
{
    /// <summary>
    /// Crea un <see cref="IAiService"/> con la configurazione corrente,
    /// oppure <c>null</c> se nessun provider è disponibile.
    /// </summary>
    Task<IAiService?> CreateAsync(CancellationToken ct);
}

/// <inheritdoc />
/// <remarks>
/// Con il provider <c>auto</c> (default) l'ordine è:
/// 1. <b>GitHub Copilot</b> — se c'è un token GitHub e il runtime è già stato scaricato
///    (scegliendo <c>copilot</c> esplicitamente il runtime si scarica al primo uso)
/// 2. <b>Claude Code</b> — se <c>claude.exe</c> è installato (abbonamento Claude)
/// 3. <b>Claude API</b> — se c'è una API key Anthropic
/// 4. <b>OpenAI-compatibile</b> — se c'è una API key (o un token GitHub per GitHub Models)
///
/// Se un provider fallisce (es. nessun abbonamento Copilot), si passa al successivo.
/// Ogni credenziale va solo al proprio servizio: il token GitHub non viene mai inviato
/// a endpoint di terzi.
/// </remarks>
internal sealed class AiServiceFactory : IAiServiceFactory
{
    /// <summary>
    /// HttpClient singleton condiviso — non va ricreato ad ogni chiamata (socket exhaustion).
    /// L'autenticazione è impostata per-richiesta dentro <see cref="AiService"/>.
    /// </summary>
    private static readonly HttpClient SharedHttpClient = new();

    private readonly ISvnAssistSettings _settings;
    private readonly IGitHubTokenResolver _tokenResolver;
    private readonly IProcessRunner _processRunner;
    private readonly Func<string?> _claudeLocator;
    private readonly Func<string, string?> _environmentReader;
    private readonly Func<bool> _copilotRuntimeInstalled;

    public AiServiceFactory(
        ISvnAssistSettings settings,
        IGitHubTokenResolver tokenResolver,
        IProcessRunner? processRunner = null,
        Func<string?>? claudeLocator = null,
        Func<string, string?>? environmentReader = null,
        Func<bool>? copilotRuntimeInstalled = null)
    {
        _settings = settings;
        _tokenResolver = tokenResolver;
        _processRunner = processRunner ?? new ProcessRunner();
        _claudeLocator = claudeLocator ?? ClaudeCodeAiService.FindClaudeExecutable;
        _environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
        _copilotRuntimeInstalled = copilotRuntimeInstalled ?? (() => CopilotRuntimeInstaller.IsInstalled);
    }

    /// <inheritdoc />
    public async Task<IAiService?> CreateAsync(CancellationToken ct)
    {
        var provider = (await _settings.GetAiProviderAsync(ct)).Trim().ToLowerInvariant();
        bool Use(string name) => provider == name || provider is "auto" or "";

        var candidates = new List<(string Name, IAiService Service)>();

        // Copilot scelto esplicitamente: il runtime (~100 MB) si scarica al primo uso.
        // In auto lo usiamo solo se è già installato, per non avviare download a sorpresa.
        if (provider == "copilot" || (Use("copilot") && _copilotRuntimeInstalled()))
        {
            var token = await _tokenResolver.ResolveAsync(ct);
            // Come nel checklist: senza token GitHub (login gh) Copilot non è utilizzabile nemmeno se scelto,
            // così "nessun provider" apre la guida al login invece di un errore oscuro dal runtime.
            if (token is not null)
                candidates.Add(("GitHub Copilot", new CopilotAiService(SharedHttpClient, token, NormalizeCopilotModel(await _settings.GetCopilotModelAsync(ct)))));
        }

        if (Use("claudecode") && _claudeLocator() is { } claudeExe)
            candidates.Add(("Claude Code", new ClaudeCodeAiService(_processRunner, claudeExe, ResolveClaudeModel(await _settings.GetClaudeModelAsync(ct)))));

        if (Use("claude"))
        {
            var key = await _settings.GetAnthropicApiKeyAsync(ct);
            if (!string.IsNullOrWhiteSpace(key) || !string.IsNullOrWhiteSpace(_environmentReader("ANTHROPIC_API_KEY")))
                candidates.Add(("Claude API", new AnthropicAiService(key, ResolveClaudeModel(await _settings.GetClaudeModelAsync(ct)))));
        }

        if (Use("openai") && await CreateOpenAiCompatibleAsync(ct) is { } openAi)
            candidates.Add((Loc.T("OpenAI-compatibile"), openAi));

        return candidates.Count switch
        {
            0 => null,
            1 => candidates[0].Service,
            _ => new FallbackAiService(candidates),
        };
    }

    private async Task<IAiService?> CreateOpenAiCompatibleAsync(CancellationToken ct)
    {
        var endpoint = await _settings.GetAiEndpointAsync(ct);
        var model = NormalizeOpenAiModel(endpoint, await _settings.GetAiModelAsync(ct));

        // La key esplicita nelle opzioni vince. In mancanza, il token GitHub rilevato in automatico
        // si usa SOLO verso GitHub Models: mandarlo a un altro endpoint esporrebbe il tuo account GitHub.
        var apiKey = await _settings.GetAiApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(apiKey) && IsGitHubModelsEndpoint(endpoint))
            apiKey = await _tokenResolver.ResolveAsync(ct);

        return string.IsNullOrWhiteSpace(apiKey) ? null : new AiService(SharedHttpClient, endpoint, model, apiKey);
    }

    /// <summary>Un valore salvato fuori lista (vecchie versioni con testo libero, refusi) ricade sul default invece di rompere la chiamata.</summary>
    internal static string ResolveClaudeModel(string? model)
        => SettingDefinitions.KnownClaudeModels.Contains(model?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            ? model!.Trim()
            : SettingDefinitions.DefaultClaudeModel;

    /// <summary>"default" (o vuoto) significa modello predefinito di Copilot: al servizio si passa la stringa vuota.</summary>
    internal static string NormalizeCopilotModel(string? model)
        => string.IsNullOrWhiteSpace(model) || string.Equals(model.Trim(), SettingDefinitions.CopilotDefaultModel, StringComparison.OrdinalIgnoreCase)
           || !SettingDefinitions.KnownCopilotModels.Contains(model.Trim(), StringComparer.OrdinalIgnoreCase)
            ? string.Empty
            : model.Trim();

    /// <summary>
    /// GitHub Models vuole l'id con publisher (<c>openai/gpt-4o-mini</c>), gli altri endpoint OpenAI-compatibili
    /// (OpenAI, Azure, Ollama) il nome nudo (<c>gpt-4o-mini</c>). Converte in entrambi i versi, così
    /// funzionano anche i valori salvati con le versioni precedenti.
    /// </summary>
    internal static string NormalizeOpenAiModel(string endpoint, string? model)
    {
        var name = string.IsNullOrWhiteSpace(model) ? SettingDefinitions.DefaultAiModel : model.Trim();
        const string prefix = "openai/";
        if (IsGitHubModelsEndpoint(endpoint))
        {
            // Su GitHub Models la lista è chiusa: un id fuori lista (vecchio valore salvato) ricade sul default.
            var full = name.Contains('/') ? name : prefix + name;
            return SettingDefinitions.KnownAiModels.Contains(full, StringComparer.OrdinalIgnoreCase) ? full : SettingDefinitions.DefaultAiModel;
        }

        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name[prefix.Length..] : name;
    }

    internal static bool IsGitHubModelsEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "models.github.ai", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Prova i provider in ordine: al primo che risponde restituisce il risultato,
/// altrimenti riporta gli errori di tutti.
/// </summary>
internal sealed class FallbackAiService : IAiService
{
    private readonly IReadOnlyList<(string Name, IAiService Service)> _providers;

    public FallbackAiService(IReadOnlyList<(string Name, IAiService Service)> providers) => _providers = providers;

    /// <summary>Nomi dei provider nell'ordine in cui vengono provati.</summary>
    public IEnumerable<string> ProviderNames => _providers.Select(p => p.Name);

    /// <summary>Nome del provider che ha risposto all'ultima richiesta riuscita (<c>null</c> se nessuna).</summary>
    public string? LastUsedProvider { get; private set; }

    public Task<string> GenerateCommitMessageAsync(string diff, string language, CancellationToken ct, string? projectInstructions = null)
        => TryEachAsync(s => s.GenerateCommitMessageAsync(diff, language, ct, projectInstructions), ct);

    public Task<string> ImproveCommitMessageAsync(string draft, string diff, string language, CancellationToken ct, string? projectInstructions = null)
        => TryEachAsync(s => s.ImproveCommitMessageAsync(draft, diff, language, ct, projectInstructions), ct);

    private async Task<string> TryEachAsync(Func<IAiService, Task<string>> call, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var (name, service) in _providers)
        {
            try
            {
                var result = await call(service);
                LastUsedProvider = name;
                return result;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                errors.Add($"{name}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(Loc.T("Nessun provider AI ha risposto.") + "\n" + string.Join("\n", errors));
    }
}
