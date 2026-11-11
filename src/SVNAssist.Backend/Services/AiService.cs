using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SVNAssist.Services;

/// <summary>
/// Provider AI per endpoint compatibili con OpenAI Chat Completions
/// (GitHub Models, OpenAI, Azure OpenAI, Ollama, LM Studio...).
/// </summary>
/// <remarks>
/// Perché REST generico e non l'SDK OpenAI?
/// - Funziona con qualsiasi provider compatibile
/// - Nessuna dipendenza aggiuntiva da NuGet
///
/// <see cref="HttpClient"/> viene iniettato come singleton: non va creato per ogni
/// chiamata (rischio socket exhaustion). L'header <c>Authorization</c> è impostato
/// <b>per singola richiesta</b>, non sui <c>DefaultRequestHeaders</c> del client condiviso,
/// così più istanze non si sovrascrivono a vicenda.
/// </remarks>
public class AiService : AiServiceBase
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _apiKey;

    /// <summary>Crea il provider OpenAI-compatibile.</summary>
    /// <param name="httpClient">Client HTTP singleton condiviso.</param>
    /// <param name="endpoint">URL completo dell'endpoint Chat Completions.</param>
    /// <param name="model">Nome del modello (es. <c>gpt-4o-mini</c>).</param>
    /// <param name="apiKey">
    /// Inviata come <c>Authorization: Bearer</c>. Se vuota, nessun header
    /// (utile per endpoint locali come Ollama).
    /// </param>
    public AiService(HttpClient httpClient, string endpoint, string model, string apiKey)
    {
        _httpClient = httpClient;
        _endpoint = endpoint;
        _model = model;
        _apiKey = apiKey;
    }

    /// <inheritdoc />
    protected override Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct)
    {
        EnsureSafeEndpoint(_endpoint, _apiKey);
        return CallChatCompletionsAsync(systemPrompt, userMessage, ct, allowRetryOnLimit: true);
    }

    /// <summary>
    /// Una chiave inviata in chiaro (<c>http://</c>) a un host remoto sarebbe leggibile da chiunque sul percorso:
    /// si rifiuta, salvo endpoint locali (loopback, es. Ollama) dove non esce dalla macchina.
    /// </summary>
    /// <exception cref="InvalidOperationException">Chiave configurata con un endpoint http non loopback.</exception>
    internal static void EnsureSafeEndpoint(string endpoint, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return;

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"L'endpoint AI \"{endpoint}\" non è un URL valido.");

        if (uri.Scheme == Uri.UriSchemeHttps)
            return;

        var isLoopback = uri.IsLoopback
                         || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                         || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
        if (uri.Scheme != Uri.UriSchemeHttp || !isLoopback)
            throw new InvalidOperationException(
                $"Rifiuto di inviare la chiave API a {uri.Host} con una connessione non cifrata ({uri.Scheme}://). " +
                "Usa un endpoint https:// (le sole eccezioni sono gli endpoint locali come http://localhost).");
    }

    /// <remarks>
    /// Formato OpenAI: <c>{ model, max_tokens, messages: [system, user] }</c>.
    /// <c>max_tokens: 500</c>: abbastanza per titolo e corpo, senza lasciar divagare il modello.
    /// </remarks>
    private async Task<string> CallChatCompletionsAsync(
        string systemPrompt,
        string userMessage,
        CancellationToken ct,
        bool allowRetryOnLimit)
    {
        var requestBody = new
        {
            model = _model,
            max_tokens = 500,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _httpClient.SendAsync(request, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            if (allowRetryOnLimit && IsTokenLimitError(response.StatusCode, responseJson))
            {
                var reducedMessage = TrimToMaxChars(userMessage, MaxUserMessageChars / 2);
                return await CallChatCompletionsAsync(systemPrompt, reducedMessage, ct, allowRetryOnLimit: false);
            }

            // Il corpo d'errore del server può citare la chiave (es. "invalid key: sk-..."): la si oscura.
            var hint = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && AiServiceFactory.IsGitHubModelsEndpoint(_endpoint)
                ? " Il token GitHub (anche quello di gh) potrebbe non avere il permesso per GitHub Models: crea un token con permesso models:read e incollalo in Tools > Options > SVNAssist > AI API Key."
                : string.Empty;
            throw new HttpRequestException(
                $"AI API error {(int)response.StatusCode} {response.StatusCode}: {RedactSecret(responseJson)}{hint}");
        }

        return ParseCompletionResponse(responseJson);
    }

    private string RedactSecret(string text)
        => string.IsNullOrWhiteSpace(_apiKey) ? text : text.Replace(_apiKey, "***", StringComparison.Ordinal);

    /// <summary>Estrae <c>choices[0].message.content</c> dalla risposta JSON.</summary>
    private static string ParseCompletionResponse(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
            throw new InvalidOperationException("La risposta AI non contiene alcun messaggio.");

        var messageContent = choices[0].GetProperty("message").GetProperty("content").GetString();

        return messageContent?.Trim()
            ?? throw new InvalidOperationException("Il contenuto del messaggio AI è null.");
    }

    private static bool IsTokenLimitError(HttpStatusCode statusCode, string responseJson)
    {
        if (statusCode == HttpStatusCode.RequestEntityTooLarge)
            return true;

        return responseJson.Contains("tokens_limit_reached", StringComparison.OrdinalIgnoreCase)
               || responseJson.Contains("Request body too large", StringComparison.OrdinalIgnoreCase);
    }
}
