using System.Net.Http;

namespace SVNAssist.Services;

/// <summary>
/// Crea istanze di <see cref="IAiService"/> configurate con le impostazioni correnti
/// (endpoint, modello, API key) lette da <see cref="SettingsService"/>.
/// </summary>
/// <remarks>
/// Astrarre la creazione dietro una factory permette al ViewModel di <b>non</b> conoscere
/// né <see cref="AiService"/> né come si legge la configurazione: riceve solo
/// <see cref="IAiServiceFactory"/> via costruttore, diventando così unit-testabile
/// (basta iniettare una factory finta). Rispetta DIP e SRP.
/// </remarks>
internal interface IAiServiceFactory
{
    /// <summary>
    /// Crea un <see cref="IAiService"/> con la configurazione corrente,
    /// oppure <c>null</c> se l'AI non è configurata (API key mancante).
    /// </summary>
    Task<IAiService?> CreateAsync(CancellationToken ct);
}

/// <inheritdoc />
internal sealed class AiServiceFactory : IAiServiceFactory
{
    /// <summary>
    /// HttpClient singleton condiviso — non va ricreato ad ogni chiamata (socket exhaustion).
    /// L'autenticazione è impostata per-richiesta dentro <see cref="AiService"/>, quindi
    /// condividere il client tra più istanze è sicuro.
    /// </summary>
    private static readonly HttpClient SharedHttpClient = new();

    private readonly ISvnAssistSettings _settingsService;

    public AiServiceFactory(ISvnAssistSettings settingsService)
    {
        _settingsService = settingsService;
    }

    /// <inheritdoc />
    public async Task<IAiService?> CreateAsync(CancellationToken ct)
    {
        var endpoint = await _settingsService.GetAiEndpointAsync(ct);
        var apiKey = await _settingsService.GetAiApiKeyAsync(ct);
        var model = await _settingsService.GetAiModelAsync(ct);

        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        return new AiService(SharedHttpClient, endpoint, model, apiKey);
    }
}
