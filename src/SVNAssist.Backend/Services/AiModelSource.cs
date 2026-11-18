using System.Net.Http;
using System.Net.Http.Headers;

namespace SVNAssist.Services;

/// <summary>
/// Elenco modelli dai provider reali. Per ogni provider:
/// <list type="bullet">
/// <item><b>copilot</b>: <c>CopilotClient.ListModelsAsync</c> (SDK GitHub.Copilot.SDK) con moltiplicatore di richieste premium e account.</item>
/// <item><b>claude</b> / <b>claudecode</b>: <c>GET https://api.anthropic.com/v1/models</c> (serve la API key; senza chiave Claude Code usa l'elenco predefinito).</item>
/// <item><b>openai</b>: GitHub Models <c>GET https://models.github.ai/catalog/models</c>, altrimenti <c>GET {base}/models</c> dell'endpoint configurato.</item>
/// </list>
/// Le credenziali vanno solo al proprio provider (il token GitHub solo a models.github.ai) e non finiscono mai nei messaggi d'errore.
/// </summary>
internal sealed class AiModelSource : IAiModelSource
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly ISvnAssistSettings _settings;
    private readonly IGitHubTokenResolver _tokens;
    private readonly Func<string, string?> _env;

    public AiModelSource(HttpClient http, ISvnAssistSettings settings, IGitHubTokenResolver tokens, Func<string, string?>? environmentReader = null)
    {
        _http = http;
        _settings = settings;
        _tokens = tokens;
        _env = environmentReader ?? Environment.GetEnvironmentVariable;
    }

    public async Task<string> IdentityAsync(string provider, CancellationToken ct)
        => provider == "openai" ? (await _settings.GetAiEndpointAsync(ct)).Trim() : string.Empty;

    public async Task<AiModelsFetched?> ListAsync(string provider, bool force, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(provider == "copilot" ? TimeSpan.FromMinutes(3) : RequestTimeout); // Copilot: puo' dover avviare il runtime
        try
        {
            return provider switch
            {
                "copilot" => await ListCopilotAsync(force, timeout.Token),
                "claude" or "claudecode" => await ListAnthropicAsync(provider, timeout.Token),
                "openai" => await ListOpenAiAsync(timeout.Token),
                "gemini" => await ListGeminiAsync(timeout.Token),
                _ => null,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException(Loc.T("Il provider non ha risposto in tempo: riprova con \"Aggiorna elenco\"."));
        }
    }

    private async Task<AiModelsFetched> ListCopilotAsync(bool force, CancellationToken ct)
    {
        var token = await _tokens.ResolveAsync(ct)
            ?? throw new InvalidOperationException(Loc.T("Per elencare i modelli di Copilot serve il login a GitHub (gh auth login): torna al passo Accesso."));
        var (raw, account, defaultModel) = await CopilotAiService.ListModelsAsync(_http, token, force, ct);
        return new AiModelsFetched(AiModelCatalog.BuildCopilotModels(raw, defaultModel), account);
    }

    private async Task<AiModelsFetched?> ListAnthropicAsync(string provider, CancellationToken ct)
    {
        var key = (await _settings.GetAnthropicApiKeyAsync(ct)).Trim();
        if (key.Length == 0)
            key = (_env("ANTHROPIC_API_KEY") ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            // Claude Code usa l'abbonamento, non la chiave: senza chiave non si puo' elencare, resta l'elenco predefinito + "Altro...".
            return provider == "claudecode"
                ? null
                : throw new InvalidOperationException(Loc.T("Per elencare i modelli Claude serve la API key Anthropic (passo Accesso)."));
        }

        var all = new List<AiModelInfo>();
        string? after = null;
        for (var page = 0; page < 10; page++)
        {
            var url = "https://api.anthropic.com/v1/models?limit=1000" + (after is null ? string.Empty : "&after_id=" + Uri.EscapeDataString(after));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("x-api-key", key);
            request.Headers.Add("anthropic-version", "2023-06-01");
            var (models, hasMore, last) = AiModelCatalog.ParseAnthropicModels(await SendAsync(request, key, ct));
            all.AddRange(models);
            if (!hasMore || last is null)
                break;
            after = last;
        }

        return new AiModelsFetched(all);
    }

    private async Task<AiModelsFetched> ListOpenAiAsync(CancellationToken ct)
    {
        var endpoint = await _settings.GetAiEndpointAsync(ct);
        var url = AiModelCatalog.ModelsUrlFor(endpoint);
        var key = (await _settings.GetAiApiKeyAsync(ct)).Trim();
        var gitHubModels = AiServiceFactory.IsGitHubModelsEndpoint(endpoint);
        // Il token GitHub rilevato si usa SOLO verso GitHub Models; per il catalogo e' facoltativo.
        if (key.Length == 0 && gitHubModels)
            key = (await _tokens.ResolveAsync(ct) ?? string.Empty).Trim();
        AiService.EnsureSafeEndpoint(url, key);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (key.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (gitHubModels)
        {
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        }

        var json = await SendAsync(request, key, ct);
        return new AiModelsFetched(gitHubModels ? AiModelCatalog.ParseGitHubModelsCatalog(json) : AiModelCatalog.ParseOpenAiModels(json));
    }

    /// <summary>Gemini: <c>GET {base}/models</c> con la API key; senza chiave "Non collegato" (nessun elenco, mai uno scritto a mano).</summary>
    private async Task<AiModelsFetched?> ListGeminiAsync(CancellationToken ct)
    {
        var key = (await _settings.GetAiApiKeyAsync(ct)).Trim();
        if (key.Length == 0)
            return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, AiServiceFactory.GeminiEndpoint + "/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return new AiModelsFetched(AiModelCatalog.ParseOpenAiModels(await SendAsync(request, key, ct)));
    }

    /// <summary>Esegue la richiesta; se non e' un successo lancia con lo stato e il corpo (senza la chiave) cosi' l'utente vede l'errore vero.</summary>
    private async Task<string> SendAsync(HttpRequestMessage request, string secret, CancellationToken ct)
    {
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var clean = (secret.Length > 0 ? body.Replace(secret, "***", StringComparison.Ordinal) : body).Replace('\n', ' ').Replace('\r', ' ').Trim();
            throw new HttpRequestException(Loc.T("Elenco modelli non disponibile (HTTP {0}): {1}", (int)response.StatusCode, clean.Length > 300 ? clean[..300] + "..." : clean));
        }

        return body;
    }
}
