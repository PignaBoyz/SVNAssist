using SVNAssist.Rpc;

namespace SVNAssist.Services;

/// <summary>
/// Chiavi, valori ammessi e default delle impostazioni di SVNAssist (stessi valori della pagina Options di Visual Studio).
/// L'host le legge/scrive nella sua pagina Tools > Options e le passa al backend con <c>IHost.GetSettingsAsync</c>:
/// l'elenco (con le etichette) e' in docs/ui-protocol.md, sezione "Impostazioni".
/// </summary>
internal static class SettingDefinitions
{
    /// <summary>Endpoint attuale di GitHub Models (compatibile OpenAI).</summary>
    internal const string GitHubModelsEndpoint = "https://models.github.ai/inference/chat/completions";

    /// <summary>Vecchio endpoint di GitHub Models, dismesso (il dominio non risolve piu').</summary>
    internal const string LegacyGitHubModelsEndpoint = "https://models.inference.ai.azure.com/chat/completions";

    /// <summary>Modello Claude di default: Sonnet, il compromesso migliore costo/qualita' per i commit message.</summary>
    internal const string DefaultClaudeModel = "claude-sonnet-5-5";

    // Nessun elenco di modelli nell'app: i modelli disponibili si chiedono al provider collegato (AiModelCatalog) e l'impostazione e' testo libero.
    // Qui restano solo i valori predefiniti con cui l'AI funziona al primo uso.

    /// <summary>Valore "modello predefinito di Copilot" (l'enum non ammette valori vuoti).</summary>
    internal const string CopilotDefaultModel = "default";

    /// <summary>Modello di default per GitHub Models: l'endpoint richiede l'id con prefisso del publisher.</summary>
    internal const string DefaultAiModel = "openai/gpt-4o-mini";

    /// <summary>Modello Gemini di default.</summary>
    internal const string DefaultGeminiModel = "gemini-2.5-flash";

    // Chiavi (camelCase; il backend le legge senza distinguere maiuscole/minuscole).
    internal const string AiProvider = "aiProvider";
    internal const string ClaudeModel = "claudeModel";
    internal const string AnthropicApiKey = "anthropicApiKey";
    internal const string CopilotModel = "copilotModel";
    internal const string AiEndpoint = "aiEndpoint";
    internal const string AiApiKey = "aiApiKey";
    internal const string AiModel = "aiModel";
    internal const string GeminiApiKey = "geminiApiKey";
    internal const string GeminiModel = "geminiModel";
    internal const string DefaultLanguage = "defaultLanguage";

    /// <summary>true = il messaggio generato dall'AI scrive anche modello e costo (dove il provider lo dichiara). Spento di default.</summary>
    internal const string AiShowUsage = "aiShowUsage";

    /// <summary>true = prima di SVN Update si vedono le revisioni in arrivo con il riassunto AI. Spento di default.</summary>
    internal const string AiExplainIncoming = "aiExplainIncoming";
    internal const string WorkingCopyPath = "workingCopyPath";
    internal const string WindowTheme = "windowTheme";

    /// <summary>Nomi delle cartelle trunk/branches/tags (separati da /), per elencare i rami di SVN Switch.</summary>
    internal const string RepoLayout = "repoLayout";

    /// <summary>Ogni quanti minuti controllare se altri hanno committato sul ramo (0 = spento).</summary>
    internal const string HeadCheckMinutes = "headCheckMinutes";

    internal const int DefaultHeadCheckMinutes = 5;

    /// <summary>Lingua dell'interfaccia: auto | it | en (l'host manda anche <c>uiLanguageEffective</c>, gia' risolta).</summary>
    internal const string UiLanguage = "uiLanguage";
    internal const string UiLanguageEffective = "uiLanguageEffective";

    /// <summary>Modalita' leggera: disattiva FileSystemWatcher continuo e polling HEAD periodico per PC datati / solution grandi.</summary>
    internal const string LightweightMode = "lightweightMode";

    // Azure DevOps (facoltativo, spento): organizzazione, progetto, token PAT (segreto) e cosa fare dopo i commit.
    internal const string AzEnabled = "azEnabled", AzOrg = "azOrg", AzProject = "azProject", AzPat = "azPat",
        AzLinkOnCommit = "azLinkOnCommit", AzComment = "azComment", AzAuth = "azAuth", AzClientId = "azClientId", AzTenant = "azTenant";

    /// <summary>Tutte le chiavi con il loro valore di default (usato quando l'host non ne fornisce una).</summary>
    internal static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [AiProvider] = "auto",
        [ClaudeModel] = DefaultClaudeModel,
        [AnthropicApiKey] = "",
        [CopilotModel] = CopilotDefaultModel,
        [AiEndpoint] = GitHubModelsEndpoint,
        [AiApiKey] = "",
        [AiModel] = DefaultAiModel,
        [GeminiApiKey] = "",
        [GeminiModel] = DefaultGeminiModel,
        [DefaultLanguage] = "auto",
        [AiShowUsage] = "false",
        [AiExplainIncoming] = "false",
        [WorkingCopyPath] = "",
        [WindowTheme] = "auto",
        [RepoLayout] = SVNAssist.Models.RepoLayout.Default,
        [HeadCheckMinutes] = DefaultHeadCheckMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        [LightweightMode] = "false",
        [AzEnabled] = "false",
        [AzOrg] = "",
        [AzProject] = "",
        [AzPat] = "",
        [AzLinkOnCommit] = "true",
        [AzComment] = "false",
        [AzAuth] = "pat",
        [AzClientId] = "",
        [AzTenant] = "",
        // UiLanguage non ha default qui: senza il valore dell'host (es. nei test) l'interfaccia resta in italiano.
    };
}

/// <summary>
/// Astrazione delle impostazioni di SVNAssist lette a runtime: disaccoppia i consumatori
/// (store, <see cref="AiServiceFactory"/>) dall'host e permette di iniettare impostazioni finte nei test.
/// </summary>
internal interface ISvnAssistSettings
{
    /// <summary>Provider AI scelto (auto/copilot/claudecode/claude/openai).</summary>
    Task<string> GetAiProviderAsync(CancellationToken ct);

    /// <summary>Modello Claude.</summary>
    Task<string> GetClaudeModelAsync(CancellationToken ct);

    /// <summary>API key Anthropic.</summary>
    Task<string> GetAnthropicApiKeyAsync(CancellationToken ct);

    /// <summary>Modello GitHub Copilot (<c>default</c> o vuoto = predefinito di Copilot).</summary>
    Task<string> GetCopilotModelAsync(CancellationToken ct);

    /// <summary>Endpoint AI (formato OpenAI Chat Completions).</summary>
    Task<string> GetAiEndpointAsync(CancellationToken ct);

    /// <summary>API key/PAT per l'endpoint AI.</summary>
    Task<string> GetAiApiKeyAsync(CancellationToken ct);

    /// <summary>Nome del modello AI.</summary>
    Task<string> GetAiModelAsync(CancellationToken ct);

    /// <summary>API key dedicata a Gemini (Google AI Studio).</summary>
    Task<string> GetGeminiApiKeyAsync(CancellationToken ct) => Task.FromResult(string.Empty);

    /// <summary>Nome del modello Gemini.</summary>
    Task<string> GetGeminiModelAsync(CancellationToken ct) => Task.FromResult(SettingDefinitions.DefaultGeminiModel);

    /// <summary>Se disattivare watcher ricorsivo e controlli periodici per PC datati / solution grandi (default: no).</summary>
    Task<bool> GetLightweightModeAsync(CancellationToken ct) => Task.FromResult(false);

    /// <summary>Lingua predefinita per i commit message (auto/italiano/english).</summary>
    Task<string> GetDefaultLanguageAsync(CancellationToken ct);

    /// <summary>Se scrivere modello e costo nel messaggio generato dall'AI (default: no).</summary>
    Task<bool> GetAiShowUsageAsync(CancellationToken ct) => Task.FromResult(false);

    /// <summary>Se SVN Update mostra prima le revisioni in arrivo con il riassunto AI (default: no).</summary>
    Task<bool> GetAiExplainIncomingAsync(CancellationToken ct) => Task.FromResult(false);

    /// <summary>Percorso della working copy SVN (vuoto se auto-rilevato).</summary>
    Task<string> GetWorkingCopyPathAsync(CancellationToken ct);

    /// <summary>Layout del repository: "trunk/branches/tags" o i nomi scelti dall'utente.</summary>
    Task<string> GetRepoLayoutAsync(CancellationToken ct) => Task.FromResult(SVNAssist.Models.RepoLayout.Default);

    /// <summary>Minuti fra due controlli "altri hanno committato?" (0 = spento; il default dell'interfaccia e' spento, quello vero lo da <see cref="HostSettings"/>).</summary>
    Task<int> GetHeadCheckMinutesAsync(CancellationToken ct) => Task.FromResult(0);

    /// <summary>Tema della finestra (auto/light/dark/blue).</summary>
    Task<string> GetWindowThemeAsync(CancellationToken ct);

    /// <summary>Lingua effettiva dell'interfaccia ("it" o "en"): l'host risolve "auto" con la lingua di Visual Studio.</summary>
    Task<string> GetUiLanguageAsync(CancellationToken ct) => Task.FromResult("it");

    /// <summary>Salva il tema della finestra (scrive nelle impostazioni dell'host).</summary>
    Task SetWindowThemeAsync(string mode, CancellationToken ct);

    /// <summary>Integrazione Azure DevOps (spenta se l'host non la fornisce).</summary>
    Task<AzureSettings> GetAzureAsync(CancellationToken ct) => Task.FromResult(AzureSettings.Off);

    /// <summary>Salva un'impostazione (chiavi di <see cref="SettingDefinitions"/>) nelle impostazioni dell'host: la usa la procedura guidata dell'AI.</summary>
    Task SetSettingAsync(string key, string value, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>
/// Impostazioni lette dall'host (<see cref="IHost.GetSettingsAsync"/>) e tenute in cache fino a
/// <see cref="Invalidate"/> (chiamato da <c>IBackend.SettingsChangedAsync</c>).
/// </summary>
internal sealed class HostSettings : ISvnAssistSettings, IDisposable
{
    private readonly IHost _host;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _cache;

    public HostSettings(IHost host) => _host = host;

    /// <summary>Scarta la cache: la prossima lettura richiama l'host.</summary>
    public void Invalidate() => Volatile.Write(ref _cache, null);

    private async Task<string> GetAsync(string key, CancellationToken ct)
    {
        var cache = Volatile.Read(ref _cache);
        if (cache is null)
        {
            await _gate.WaitAsync(ct);
            try
            {
                cache = Volatile.Read(ref _cache);
                if (cache is null)
                {
                    Dictionary<string, string>? fromHost = null;
                    try
                    {
                        fromHost = await _host.GetSettingsAsync();
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        // Host irraggiungibile o errore: si lavora con i default (senza memorizzarli, si riprova al prossimo giro).
                        BackendLog.Warn("GetSettingsAsync fallita: " + ex.Message);
                    }

                    // L'host manda camelCase e alias minuscoli: chiavi uguali ignorando le maiuscole, quindi si assegna (il costruttore a copia lancia).
                    cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in fromHost ?? [])
                        cache[kv.Key] = kv.Value;
                    if (fromHost is not null)
                        Volatile.Write(ref _cache, cache);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        return cache.TryGetValue(key, out var value) && value is not null
            ? value
            : SettingDefinitions.Defaults.GetValueOrDefault(key, string.Empty);
    }

    public Task<string> GetAiProviderAsync(CancellationToken ct) => GetAsync(SettingDefinitions.AiProvider, ct);
    public async Task<bool> GetAiShowUsageAsync(CancellationToken ct) => string.Equals(await GetAsync(SettingDefinitions.AiShowUsage, ct), "true", StringComparison.OrdinalIgnoreCase);
    public async Task<bool> GetAiExplainIncomingAsync(CancellationToken ct) => string.Equals(await GetAsync(SettingDefinitions.AiExplainIncoming, ct), "true", StringComparison.OrdinalIgnoreCase);
    public Task<string> GetClaudeModelAsync(CancellationToken ct) => GetAsync(SettingDefinitions.ClaudeModel, ct);
    public Task<string> GetAnthropicApiKeyAsync(CancellationToken ct) => GetAsync(SettingDefinitions.AnthropicApiKey, ct);
    public Task<string> GetCopilotModelAsync(CancellationToken ct) => GetAsync(SettingDefinitions.CopilotModel, ct);
    public Task<string> GetAiApiKeyAsync(CancellationToken ct) => GetAsync(SettingDefinitions.AiApiKey, ct);
    public Task<string> GetAiModelAsync(CancellationToken ct) => GetAsync(SettingDefinitions.AiModel, ct);
    public Task<string> GetGeminiApiKeyAsync(CancellationToken ct) => GetAsync(SettingDefinitions.GeminiApiKey, ct);
    public Task<string> GetGeminiModelAsync(CancellationToken ct) => GetAsync(SettingDefinitions.GeminiModel, ct);
    public async Task<bool> GetLightweightModeAsync(CancellationToken ct) => string.Equals(await GetAsync(SettingDefinitions.LightweightMode, ct), "true", StringComparison.OrdinalIgnoreCase);
    public Task<string> GetDefaultLanguageAsync(CancellationToken ct) => GetAsync(SettingDefinitions.DefaultLanguage, ct);
    public Task<string> GetWorkingCopyPathAsync(CancellationToken ct) => GetAsync(SettingDefinitions.WorkingCopyPath, ct);
    public Task<string> GetWindowThemeAsync(CancellationToken ct) => GetAsync(SettingDefinitions.WindowTheme, ct);
    public Task<string> GetRepoLayoutAsync(CancellationToken ct) => GetAsync(SettingDefinitions.RepoLayout, ct);

    /// <summary>Intero 0..1440; un valore illeggibile ricade sul default (5), non spegne il controllo.</summary>
    public async Task<int> GetHeadCheckMinutesAsync(CancellationToken ct)
        => int.TryParse((await GetAsync(SettingDefinitions.HeadCheckMinutes, ct)).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? Math.Min(n, 24 * 60) : SettingDefinitions.DefaultHeadCheckMinutes;

    /// <summary>Legge l'endpoint AI. Chi aveva salvato il vecchio endpoint (dismesso) passa in automatico al nuovo.</summary>
    public async Task<string> GetAiEndpointAsync(CancellationToken ct)
    {
        var endpoint = await GetAsync(SettingDefinitions.AiEndpoint, ct);
        if (string.IsNullOrWhiteSpace(endpoint))
            return SettingDefinitions.GitHubModelsEndpoint;

        return string.Equals(endpoint.Trim(), SettingDefinitions.LegacyGitHubModelsEndpoint, StringComparison.OrdinalIgnoreCase)
            ? SettingDefinitions.GitHubModelsEndpoint
            : endpoint;
    }

    /// <summary>Usa <c>uiLanguageEffective</c> dell'host; se manca risolve <c>uiLanguage</c> con la lingua di sistema.</summary>
    public async Task<string> GetUiLanguageAsync(CancellationToken ct)
    {
        var effective = await GetAsync(SettingDefinitions.UiLanguageEffective, ct);
        if (effective is "it" or "en")
            return effective;
        var raw = await GetAsync(SettingDefinitions.UiLanguage, ct);
        return raw == "auto" ? Loc.Resolve(raw, System.Globalization.CultureInfo.CurrentUICulture.Name) : raw == "en" ? "en" : "it";
    }

    public Task SetWindowThemeAsync(string mode, CancellationToken ct) => SetSettingAsync(SettingDefinitions.WindowTheme, mode, ct);

    public async Task<AzureSettings> GetAzureAsync(CancellationToken ct)
    {
        async Task<bool> On(string key) => string.Equals(await GetAsync(key, ct), "true", StringComparison.OrdinalIgnoreCase);
        return new AzureSettings(await On(SettingDefinitions.AzEnabled), (await GetAsync(SettingDefinitions.AzOrg, ct)).Trim(),
            (await GetAsync(SettingDefinitions.AzProject, ct)).Trim(), (await GetAsync(SettingDefinitions.AzPat, ct)).Trim(),
            await On(SettingDefinitions.AzLinkOnCommit), await On(SettingDefinitions.AzComment),
            AzureSettings.AuthModes.Contains(await GetAsync(SettingDefinitions.AzAuth, ct)) ? await GetAsync(SettingDefinitions.AzAuth, ct) : "pat",
            (await GetAsync(SettingDefinitions.AzClientId, ct)).Trim(), (await GetAsync(SettingDefinitions.AzTenant, ct)).Trim());
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken ct)
    {
        await _host.SetSettingAsync(key, value);
        Invalidate();
    }

    public void Dispose() => _gate.Dispose();
}
