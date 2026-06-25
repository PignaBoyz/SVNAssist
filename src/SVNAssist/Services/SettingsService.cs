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
        defaultValue: "https://models.inference.ai.azure.com/chat/completions");

    /// <summary>
    /// API key per l'autenticazione con l'endpoint AI.
    /// </summary>
    /// <remarks>
    /// L'API key viene salvata come stringa nelle impostazioni di VS.
    /// In un progetto di produzione andrebbe cifrata con Windows Credential Manager
    /// (pacchetto NuGet <c>CredentialManagement</c> per .NET Framework, oppure
    /// <c>System.Security.Cryptography.ProtectedData</c> / DPAPI per .NET 8).
    /// Per questo progetto didattico la salviamo nel settings store di VS,
    /// che è protetto dal profilo utente del sistema operativo.
    /// </remarks>
    [VisualStudioContribution]
    internal static Setting.String AiApiKey { get; } = new(
        "aiapikey",
        "%SVNAssist.Settings.AiApiKey%",
        SvnAssistCategory,
        defaultValue: "");

    /// <summary>
    /// Nome del modello AI da usare (es. <c>gpt-4o-mini</c>, <c>gpt-4o</c>).
    /// </summary>
    [VisualStudioContribution]
    internal static Setting.String AiModel { get; } = new(
        "aimodel",
        "%SVNAssist.Settings.AiModel%",
        SvnAssistCategory,
        defaultValue: "gpt-4o-mini");

    /// <summary>
    /// Lingua predefinita per la generazione dei commit message:
    /// <c>auto</c>, <c>italiano</c> o <c>english</c>.
    /// </summary>
    [VisualStudioContribution]
    internal static Setting.String DefaultLanguage { get; } = new(
        "defaultlanguage",
        "%SVNAssist.Settings.DefaultLanguage%",
        SvnAssistCategory,
        defaultValue: "auto");

    /// <summary>
    /// Percorso della working copy SVN.
    /// Se vuoto, viene auto-rilevato dalla solution aperta in VS.
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
        return snapshot.AiEndpoint.ValueOrDefault("https://models.inference.ai.azure.com/chat/completions");
    }

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
        return snapshot.AiModel.ValueOrDefault("gpt-4o-mini");
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
