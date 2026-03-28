using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SVNAssist.Services;

/// <summary>
/// Implementazione di <see cref="IAiService"/> che chiama un endpoint
/// compatibile con il formato OpenAI Chat Completions (<c>/v1/chat/completions</c>).
/// </summary>
/// <remarks>
/// Perché un'interfaccia REST generica e non l'SDK OpenAI?
/// - Funziona con qualsiasi provider compatibile (OpenAI, Azure OpenAI, LM Studio, Ollama, ecc.)
/// - Nessuna dipendenza aggiuntiva da NuGet
/// - Il formato Chat Completions è uno standard de facto
///
/// <see cref="HttpClient"/> viene iniettato dal costruttore come singleton —
/// non va creato per ogni chiamata (rischio socket exhaustion).
/// Endpoint, API key e modello vengono passati al costruttore
/// e in futuro arriveranno da <c>SettingsService</c> (Step 9).
/// </remarks>
public class AiService : IAiService
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _model;

    /// <summary>
    /// Crea una nuova istanza di <see cref="AiService"/>.
    /// </summary>
    /// <param name="httpClient">
    /// Client HTTP singleton — deve avere già l'header <c>Authorization: Bearer {apiKey}</c>
    /// configurato se l'endpoint lo richiede.
    /// </param>
    /// <param name="endpoint">
    /// URL completo dell'endpoint Chat Completions
    /// (es. <c>https://api.openai.com/v1/chat/completions</c>).
    /// </param>
    /// <param name="model">Nome del modello da usare (es. <c>gpt-4o-mini</c>).</param>
    public AiService(HttpClient httpClient, string endpoint, string model)
    {
        _httpClient = httpClient;
        _endpoint = endpoint;
        _model = model;
    }

    /// <inheritdoc />
    public async Task<string> GenerateCommitMessageAsync(string diff, string language, CancellationToken ct)
    {
        var languageInstruction = GetLanguageInstruction(language);

        var systemPrompt = "Sei un assistente per sviluppatori. Analizza il seguente diff SVN " +
                           "e genera un messaggio di commit chiaro e conciso. " +
                           "Rispondi solo con il messaggio, niente altro." +
                           languageInstruction;

        var userMessage = diff;

        return await CallChatCompletionsAsync(systemPrompt, userMessage, ct);
    }

    /// <inheritdoc />
    public async Task<string> ImproveCommitMessageAsync(string draft, string diff, string language, CancellationToken ct)
    {
        var languageInstruction = GetLanguageInstruction(language);

        var systemPrompt = "Sei un assistente per sviluppatori. Migliora questo messaggio di commit " +
                           "rendendolo più chiaro e professionale. " +
                           "Rispondi solo con il messaggio migliorato, niente altro." +
                           languageInstruction;

        var userMessage = $"Messaggio originale: {draft}\n\nDiff di riferimento:\n{diff}";

        return await CallChatCompletionsAsync(systemPrompt, userMessage, ct);
    }

    /// <summary>
    /// Chiama l'endpoint Chat Completions e restituisce il contenuto della risposta.
    /// </summary>
    /// <remarks>
    /// Il formato della richiesta segue la specifica OpenAI:
    /// <code>
    /// POST /v1/chat/completions
    /// {
    ///   "model": "gpt-4o-mini",
    ///   "max_tokens": 200,
    ///   "messages": [
    ///     { "role": "system", "content": "..." },
    ///     { "role": "user", "content": "..." }
    ///   ]
    /// }
    /// </code>
    /// <c>max_tokens: 200</c> perché i commit message devono essere brevi.
    /// </remarks>
    private async Task<string> CallChatCompletionsAsync(string systemPrompt, string userMessage, CancellationToken ct)
    {
        var requestBody = new
        {
            model = _model,
            max_tokens = 200,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync(_endpoint, content, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        return ParseCompletionResponse(responseJson);
    }

    /// <summary>
    /// Estrae il testo del messaggio dalla risposta JSON di Chat Completions.
    /// </summary>
    /// <remarks>
    /// La risposta ha questa struttura:
    /// <code>
    /// {
    ///   "choices": [
    ///     {
    ///       "message": {
    ///         "content": "il messaggio generato"
    ///       }
    ///     }
    ///   ]
    /// }
    /// </code>
    /// Usiamo <see cref="JsonDocument"/> per il parsing — è leggero e non richiede
    /// classi di deserializzazione dedicate per una struttura così semplice.
    /// </remarks>
    private static string ParseCompletionResponse(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var choices = root.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
            throw new InvalidOperationException("La risposta AI non contiene alcun messaggio.");

        var messageContent = choices[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return messageContent?.Trim()
            ?? throw new InvalidOperationException("Il contenuto del messaggio AI è null.");
    }

    /// <summary>
    /// Genera l'istruzione sulla lingua da aggiungere al prompt.
    /// Con <c>"auto"</c> non si specifica nulla — il modello sceglie la lingua del diff.
    /// </summary>
    private static string GetLanguageInstruction(string language)
    {
        return language.ToLowerInvariant() switch
        {
            "auto" => string.Empty,
            _ => $" Lingua: {language}.",
        };
    }
}
