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
            if (token is not null || provider == "copilot")
                candidates.Add(("GitHub Copilot", new CopilotAiService(SharedHttpClient, token, await _settings.GetCopilotModelAsync(ct))));
        }

        if (Use("claudecode") && _claudeLocator() is { } claudeExe)
            candidates.Add(("Claude Code", new ClaudeCodeAiService(_processRunner, claudeExe, await _settings.GetClaudeModelAsync(ct))));

        if (Use("claude"))
        {
            var key = await _settings.GetAnthropicApiKeyAsync(ct);
            if (!string.IsNullOrWhiteSpace(key) || !string.IsNullOrWhiteSpace(_environmentReader("ANTHROPIC_API_KEY")))
                candidates.Add(("Claude API", new AnthropicAiService(key, await _settings.GetClaudeModelAsync(ct))));
        }

        if (Use("openai") && await CreateOpenAiCompatibleAsync(ct) is { } openAi)
            candidates.Add(("OpenAI-compatibile", openAi));

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
        var model = await _settings.GetAiModelAsync(ct);

        // La key esplicita nelle opzioni vince. In mancanza, il token GitHub rilevato in automatico
        // si usa SOLO verso GitHub Models: mandarlo a un altro endpoint esporrebbe il tuo account GitHub.
        var apiKey = await _settings.GetAiApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(apiKey) && IsGitHubModelsEndpoint(endpoint))
            apiKey = await _tokenResolver.ResolveAsync(ct);

        return string.IsNullOrWhiteSpace(apiKey) ? null : new AiService(SharedHttpClient, endpoint, model, apiKey);
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

    public Task<string> GenerateCommitMessageAsync(string diff, string language, CancellationToken ct)
        => TryEachAsync(s => s.GenerateCommitMessageAsync(diff, language, ct), ct);

    public Task<string> ImproveCommitMessageAsync(string draft, string diff, string language, CancellationToken ct)
        => TryEachAsync(s => s.ImproveCommitMessageAsync(draft, diff, language, ct), ct);

    private async Task<string> TryEachAsync(Func<IAiService, Task<string>> call, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var (name, service) in _providers)
        {
            try
            {
                return await call(service);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                errors.Add($"{name}: {ex.Message}");
            }
        }

        throw new InvalidOperationException("Nessun provider AI ha risposto.\n" + string.Join("\n", errors));
    }
}
