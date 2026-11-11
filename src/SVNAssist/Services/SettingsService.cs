#pragma warning disable VSEXTPREVIEW_SETTINGS

using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Settings;

namespace SVNAssist.Services;

/// <summary>
/// Definizioni delle impostazioni dell'estensione SVNAssist.
/// Visual Studio le espone automaticamente in Tools &gt; Options &gt; SVNAssist.
/// </summary>
/// <remarks>
/// Nel modello VisualStudio.Extensibility, le impostazioni si definiscono come
/// proprietà statiche con <c>[VisualStudioContribution]</c> — VS le rileva
/// tramite il source generator e genera automaticamente la pagina Options.
///
/// Alternativa nel vecchio modello (VSSDK): si usava <c>DialogPage</c> con
/// <c>[ProvideOptionPage]</c> e <c>IVsWritableSettingsStore</c> per la
/// persistenza manuale.
///
/// <c>GenerateObserverClass = true</c> fa generare una classe Observer
/// iniettabile via DI per leggere e monitorare le impostazioni.
/// L'observer viene registrato in <see cref="SVNAssistExtension.InitializeServices"/>
/// tramite <c>AddSettingsObservers()</c>.
///
/// Gli ID dei settings devono contenere solo lettere minuscole e numeri
/// (no punti, no underscore) e iniziare con una lettera minuscola.
/// </remarks>
internal static class SettingDefinitions
{
    /// <summary>Endpoint attuale di GitHub Models (compatibile OpenAI).</summary>
    internal const string GitHubModelsEndpoint = "https://models.github.ai/inference/chat/completions";

    /// <summary>Vecchio endpoint di GitHub Models, dismesso (il dominio non risolve più).</summary>
    internal const string LegacyGitHubModelsEndpoint = "https://models.inference.ai.azure.com/chat/completions";

    [VisualStudioContribution]
    internal static SettingCategory SvnAssistCategory { get; } = new("svnassist", "%SVNAssist.Settings.Category%")
    {
        GenerateObserverClass = true,
    };

    /// <summary>
    /// URL completo dell'endpoint Chat Completions (compatibile OpenAI).
    /// </summary>
    /// <remarks>
    /// Default: GitHub Models — gratuito per chi ha un account GitHub.
    /// Usa lo stesso formato OpenAI Chat Completions, quindi funziona anche con:
    /// - OpenAI: https://api.openai.com/v1/chat/completions
    /// - Azure OpenAI: https://{instance}.openai.azure.com/openai/deployments/{model}/chat/completions
    /// - Ollama locale: http://localhost:11434/v1/chat/completions
    /// </remarks>
    [VisualStudioContribution]
    internal static Setting.String AiEndpoint { get; } = new(
        "aiendpoint",
        "%SVNAssist.Settings.AiEndpoint%",
        SvnAssistCategory,
        defaultValue: GitHubModelsEndpoint);

    /// <summary>
    /// API key per l'autenticazione con l'endpoint AI.
    /// </summary>
    /// <remarks>
    /// L'API key viene salvata come stringa nelle impostazioni di VS.
    /// DPAPI/Credential Manager non sono applicabili: l'utente scrive la chiave in chiaro nella pagina
    /// Strumenti > Opzioni, gestita da VS (<c>Setting.String</c> non ha hook di cifratura né tipo "password"),
    /// quindi non c'è un punto in cui intercettarla prima del salvataggio. Mitigazioni: la chiave non
    /// viene mai loggata né inclusa nei messaggi d'errore, e le alternative senza chiave
    /// (variabili d'ambiente, <c>gh auth</c>, login di Claude Code) sono preferite.
    /// </remarks>
    [VisualStudioContribution]
    internal static Setting.String AiApiKey { get; } = new(
        "aiapikey",
        "%SVNAssist.Settings.AiApiKey%",
        SvnAssistCategory,
        defaultValue: "");

    /// <summary>
    /// Provider AI: <c>auto</c> (prova Copilot → Claude Code → Claude API → OpenAI-compatibile),
    /// oppure uno fisso: <c>copilot</c>, <c>claudecode</c>, <c>claude</c>, <c>openai</c>.
    /// </summary>
    [VisualStudioContribution]
    internal static Setting.Enum AiProvider { get; } = new(
        "aiprovider",
        "%SVNAssist.Settings.AiProvider%",
        SvnAssistCategory,
        [
            new EnumSettingEntry("auto", "%SVNAssist.Settings.AiProvider.Auto%"),
            new EnumSettingEntry("copilot", "%SVNAssist.Settings.AiProvider.Copilot%"),
            new EnumSettingEntry("claudecode", "%SVNAssist.Settings.AiProvider.ClaudeCode%"),
            new EnumSettingEntry("claude", "%SVNAssist.Settings.AiProvider.Claude%"),
            new EnumSettingEntry("openai", "%SVNAssist.Settings.AiProvider.OpenAi%"),
        ],
        defaultValue: "auto");

    /// <summary>Modello Claude di default: Sonnet, il compromesso migliore costo/qualità per i commit message.</summary>
    internal const string DefaultClaudeModel = "claude-sonnet-5-5";

    /// <summary>Valore "modello predefinito di Copilot" (l'enum non ammette valori vuoti).</summary>
    internal const string CopilotDefaultModel = "default";

    /// <summary>Modello di default per GitHub Models: l'endpoint richiede l'id con prefisso del publisher.</summary>
    internal const string DefaultAiModel = "openai/gpt-4o-mini";

    /// <summary>
    /// Modello Claude (usato da Claude Code e Claude API): lista curata, lo stesso id vale per entrambi.
    /// </summary>
    [VisualStudioContribution]
    internal static Setting.Enum ClaudeModel { get; } = new(
        "claudemodel",
        "%SVNAssist.Settings.ClaudeModel%",
        SvnAssistCategory,
        [
            new EnumSettingEntry("claude-sonnet-5-5", "%SVNAssist.Settings.ClaudeModel.Sonnet%"),
            new EnumSettingEntry("claude-haiku-5-5", "%SVNAssist.Settings.ClaudeModel.Haiku%"),
            new EnumSettingEntry("claude-opus-5-5", "%SVNAssist.Settings.ClaudeModel.Opus%"),
        ],
        defaultValue: DefaultClaudeModel);

    /// <summary>API key Anthropic per il provider Claude API (vuota = variabile ANTHROPIC_API_KEY).</summary>
    [VisualStudioContribution]
    internal static Setting.String AnthropicApiKey { get; } = new(
        "anthropicapikey",
        "%SVNAssist.Settings.AnthropicApiKey%",
        SvnAssistCategory,
        defaultValue: "");

    /// <summary>Modello GitHub Copilot (<c>default</c> = modello predefinito di Copilot).</summary>
    [VisualStudioContribution]
    internal static Setting.Enum CopilotModel { get; } = new(
        "copilotmodel",
        "%SVNAssist.Settings.CopilotModel%",
        SvnAssistCategory,
        [
            new EnumSettingEntry("default", "%SVNAssist.Settings.CopilotModel.Default%"),
            new EnumSettingEntry("gpt-4.1", "%SVNAssist.Settings.CopilotModel.Gpt41%"),
            new EnumSettingEntry("gpt-5-mini", "%SVNAssist.Settings.CopilotModel.Gpt5Mini%"),
            new EnumSettingEntry("claude-sonnet-4.5", "%SVNAssist.Settings.CopilotModel.ClaudeSonnet%"),
        ],
        defaultValue: CopilotDefaultModel);

    /// <summary>
    /// Modello per il provider OpenAI-compatibile / GitHub Models (id nel formato di GitHub Models,
    /// <c>publisher/modello</c>: <see cref="AiServiceFactory"/> toglie il prefisso per endpoint diversi da GitHub Models).
    /// </summary>
    [VisualStudioContribution]
    internal static Setting.Enum AiModel { get; } = new(
        "aimodel",
        "%SVNAssist.Settings.AiModel%",
        SvnAssistCategory,
        [
            new EnumSettingEntry("openai/gpt-4o-mini", "%SVNAssist.Settings.AiModel.Gpt4oMini%"),
            new EnumSettingEntry("openai/gpt-4o", "%SVNAssist.Settings.AiModel.Gpt4o%"),
            new EnumSettingEntry("openai/gpt-4.1-mini", "%SVNAssist.Settings.AiModel.Gpt41Mini%"),
            new EnumSettingEntry("openai/gpt-4.1", "%SVNAssist.Settings.AiModel.Gpt41%"),
        ],
        defaultValue: DefaultAiModel);

    /// <summary>
    /// Lingua predefinita per la generazione dei commit message:
    /// <c>auto</c>, <c>italiano</c> o <c>english</c>.
    /// </summary>
    [VisualStudioContribution]
    internal static Setting.Enum DefaultLanguage { get; } = new(
        "defaultlanguage",
        "%SVNAssist.Settings.DefaultLanguage%",
        SvnAssistCategory,
        [
            new EnumSettingEntry("auto", "%SVNAssist.Settings.DefaultLanguage.Auto%"),
            new EnumSettingEntry("italiano", "%SVNAssist.Settings.DefaultLanguage.Italiano%"),
            new EnumSettingEntry("english", "%SVNAssist.Settings.DefaultLanguage.English%"),
        ],
        defaultValue: "auto");

    /// <summary>
    /// Percorso della working copy SVN.
    /// Se impostato ha la precedenza sull'auto-rilevamento; se vuoto, viene auto-rilevato dalla solution aperta in VS.
    /// </summary>
    [VisualStudioContribution]
    internal static Setting.String WorkingCopyPath { get; } = new(
        "workingcopypath",
        "%SVNAssist.Settings.WorkingCopyPath%",
        SvnAssistCategory,
        defaultValue: "");
}

/// <summary>
/// Astrazione delle impostazioni di SVNAssist lette a runtime.
/// </summary>
/// <remarks>
/// Esiste per disaccoppiare i consumatori (ViewModel, <see cref="AiServiceFactory"/>) dalla
/// classe concreta <see cref="SettingsService"/>, che dipende dall'observer generato e non è
/// istanziabile nei test. Con l'interfaccia si può iniettare un settings finto e unit-testare.
/// </remarks>
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

    /// <summary>Lingua predefinita per i commit message (auto/italiano/english).</summary>
    Task<string> GetDefaultLanguageAsync(CancellationToken ct);

    /// <summary>Percorso della working copy SVN (vuoto se auto-rilevato).</summary>
    Task<string> GetWorkingCopyPathAsync(CancellationToken ct);
}

/// <summary>
/// Servizio che legge le impostazioni dell'estensione SVNAssist.
/// Wrappa l'observer generato dal source generator per fornire un'API pulita.
/// </summary>
/// <remarks>
/// Nel vecchio modello VSSDK si usava <c>IVsWritableSettingsStore</c>.
/// Nel nuovo modello Extensibility, le impostazioni si leggono tramite
/// un observer generato automaticamente e iniettato via DI.
///
/// <see cref="SettingsService"/> fa da intermediario: riceve l'observer nel costruttore
/// e offre metodi tipizzati per leggere ogni impostazione con valori di default.
///
/// Le proprietà dello snapshot generato corrispondono ai nomi delle proprietà C#
/// nella classe <see cref="SettingDefinitions"/> (es. <c>AiEndpoint</c>, <c>AiApiKey</c>).
/// </remarks>
internal class SettingsService : ISvnAssistSettings
{
    private readonly Settings.SvnAssistCategoryObserver _observer;

    public SettingsService(Settings.SvnAssistCategoryObserver observer)
    {
        _observer = observer;
    }

    /// <summary>Legge l'endpoint AI dalle impostazioni.</summary>
    public async Task<string> GetAiEndpointAsync(CancellationToken ct)
    {
        var snapshot = await _observer.GetSnapshotAsync(ct);
        var endpoint = snapshot.AiEndpoint.ValueOrDefault(SettingDefinitions.GitHubModelsEndpoint);

        // Chi aveva salvato il vecchio endpoint (dismesso) passa in automatico al nuovo.
        return string.Equals(endpoint.Trim(), SettingDefinitions.LegacyGitHubModelsEndpoint, StringComparison.OrdinalIgnoreCase)
            ? SettingDefinitions.GitHubModelsEndpoint
            : endpoint;
    }

    /// <inheritdoc />
    public async Task<string> GetAiProviderAsync(CancellationToken ct)
        => (await _observer.GetSnapshotAsync(ct)).AiProvider.ValueOrDefault("auto");

    /// <inheritdoc />
    public async Task<string> GetClaudeModelAsync(CancellationToken ct)
        => (await _observer.GetSnapshotAsync(ct)).ClaudeModel.ValueOrDefault(SettingDefinitions.DefaultClaudeModel);

    /// <inheritdoc />
    public async Task<string> GetAnthropicApiKeyAsync(CancellationToken ct)
        => (await _observer.GetSnapshotAsync(ct)).AnthropicApiKey.ValueOrDefault("");

    /// <inheritdoc />
    public async Task<string> GetCopilotModelAsync(CancellationToken ct)
        => (await _observer.GetSnapshotAsync(ct)).CopilotModel.ValueOrDefault(SettingDefinitions.CopilotDefaultModel);

    /// <summary>Legge l'API key dalle impostazioni.</summary>
    public async Task<string> GetAiApiKeyAsync(CancellationToken ct)
    {
        var snapshot = await _observer.GetSnapshotAsync(ct);
        return snapshot.AiApiKey.ValueOrDefault("");
    }

    /// <summary>Legge il modello AI dalle impostazioni.</summary>
    public async Task<string> GetAiModelAsync(CancellationToken ct)
    {
        var snapshot = await _observer.GetSnapshotAsync(ct);
        return snapshot.AiModel.ValueOrDefault(SettingDefinitions.DefaultAiModel);
    }

    /// <summary>Legge la lingua predefinita dalle impostazioni.</summary>
    public async Task<string> GetDefaultLanguageAsync(CancellationToken ct)
    {
        var snapshot = await _observer.GetSnapshotAsync(ct);
        return snapshot.DefaultLanguage.ValueOrDefault("auto");
    }

    /// <summary>Legge il percorso della working copy dalle impostazioni.</summary>
    public async Task<string> GetWorkingCopyPathAsync(CancellationToken ct)
    {
        var snapshot = await _observer.GetSnapshotAsync(ct);
        return snapshot.WorkingCopyPath.ValueOrDefault("");
    }
}
