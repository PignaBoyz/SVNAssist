using System.IO;
using System.Net;
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
/// - Funziona con qualsiasi provider compatibile (GitHub Models, OpenAI, Azure OpenAI, Ollama, ecc.)
/// - Nessuna dipendenza aggiuntiva da NuGet
/// - Il formato Chat Completions è uno standard de facto
///
/// Provider consigliato: <b>GitHub Models</b>
/// - Endpoint: <c>https://models.inference.ai.azure.com/chat/completions</c>
/// - Auth: GitHub Personal Access Token (PAT) come Bearer token
/// - Modelli: gpt-4o-mini, gpt-4o, Phi-3, Llama, ecc.
/// - Gratuito per gli utenti GitHub (con rate limit)
/// - Non richiede di scaricare modelli — è una API cloud
///
/// Per chi ha GitHub Copilot in VS, basta creare un PAT su
/// <c>github.com/settings/tokens</c> e inserirlo nelle impostazioni.
///
/// <see cref="HttpClient"/> viene iniettato dal costruttore come singleton —
/// non va creato per ogni chiamata (rischio socket exhaustion).
/// </remarks>
public class AiService : IAiService
{
    private const int ApproxCharsPerToken = 4;
    private const int MaxRequestTokens = 8000;
    private const int MaxResponseTokens = 200;
    private const int SafetyPromptTokens = 800;
    private const int MaxUserTokens = MaxRequestTokens - MaxResponseTokens - SafetyPromptTokens;
    private const int MaxUserMessageChars = MaxUserTokens * ApproxCharsPerToken;
    private const int DiffChunkSizeChars = 12000;
    private const int MaxSummaryChars = 1200;
    private const int MaxCombinedSummaryChars = 8000;
    private const int MaxFileListChars = 6000;
    private const int MinDiffContextChars = 2000;
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _apiKey;

    /// <summary>
    /// Crea una nuova istanza di <see cref="AiService"/>.
    /// </summary>
    /// <param name="httpClient">
    /// Client HTTP singleton condiviso. L'autenticazione viene impostata
    /// <b>per singola richiesta</b>, non sui <c>DefaultRequestHeaders</c>:
    /// così più istanze possono condividere lo stesso client senza sovrascriversi
    /// l'header <c>Authorization</c> a vicenda (la mutazione dei default header
    /// di un client condiviso non è thread-safe).
    /// </param>
    /// <param name="endpoint">
    /// URL completo dell'endpoint Chat Completions
    /// (es. <c>https://api.openai.com/v1/chat/completions</c>).
    /// </param>
    /// <param name="model">Nome del modello da usare (es. <c>gpt-4o-mini</c>).</param>
    /// <param name="apiKey">
    /// API key/PAT inviata come <c>Authorization: Bearer {apiKey}</c>.
    /// Se vuota, la richiesta parte senza header di autenticazione
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
    public async Task<string> GenerateCommitMessageAsync(string diff, string language, CancellationToken ct)
    {
        var languageInstruction = GetLanguageInstruction(language);

        var systemPrompt = "Sei un assistente per sviluppatori. Analizza il seguente diff SVN " +
                           "e genera un messaggio di commit chiaro e conciso. " +
                           "Rispondi solo con il messaggio, niente altro." +
                           languageInstruction;

        var diffContext = await GetDiffContextAsync(diff, language, ct);
        var userMessage = diffContext;

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

        var diffContext = await GetDiffContextAsync(diff, language, ct);
        var userMessage = BuildImproveUserMessage(draft, diffContext);

        return await CallChatCompletionsAsync(systemPrompt, userMessage, ct);
    }

    private async Task<string> GetDiffContextAsync(string diff, string language, CancellationToken ct)
    {
        var maxFileListChars = Math.Min(MaxFileListChars, Math.Max(0, MaxUserMessageChars - MinDiffContextChars));
        var fileListContext = BuildFileListContext(diff, maxFileListChars);
        var diffBudgetChars = MaxUserMessageChars - fileListContext.Length;
        if (!string.IsNullOrWhiteSpace(fileListContext))
            diffBudgetChars -= Environment.NewLine.Length * 2;

        if (diffBudgetChars <= 0)
            return fileListContext;

        var diffContext = diff.Length <= diffBudgetChars
            ? diff
            : await SummarizeLargeDiffAsync(diff, language, ct);

        diffContext = TrimToMaxChars(diffContext, diffBudgetChars);

        if (string.IsNullOrWhiteSpace(fileListContext))
            return diffContext;

        return $"{fileListContext}\n\n{diffContext}";
    }

    private async Task<string> SummarizeLargeDiffAsync(string diff, string language, CancellationToken ct)
    {
        var languageInstruction = GetLanguageInstruction(language);
        var chunks = SplitDiffIntoChunks(diff, DiffChunkSizeChars).ToList();
        var summaries = new List<string>(chunks.Count);

        for (var i = 0; i < chunks.Count; i++)
        {
            var systemPrompt = "Sei un assistente per sviluppatori. Riassumi le modifiche del diff SVN " +
                               "in un elenco puntato breve e tecnico (max 6 punti, max 1200 caratteri). " +
                               "Rispondi solo con l'elenco." +
                               languageInstruction;
            var userMessage = $"Chunk {i + 1}/{chunks.Count}:\n{chunks[i]}";

            var summary = await CallChatCompletionsAsync(systemPrompt, userMessage, ct);
            summary = TrimToMaxChars(summary, MaxSummaryChars);
            summaries.Add(summary);
        }

        var combined = string.Join("\n", summaries);
        if (combined.Length > MaxCombinedSummaryChars)
            combined = await SummarizeTextAsync(combined, language, ct);

        return TrimToMaxChars(combined, MaxCombinedSummaryChars);
    }

    private async Task<string> SummarizeTextAsync(string text, string language, CancellationToken ct)
    {
        var languageInstruction = GetLanguageInstruction(language);
        var systemPrompt = "Sei un assistente per sviluppatori. Riassumi il testo seguente " +
                           "in un elenco puntato chiaro e tecnico (max 8 punti). " +
                           "Rispondi solo con l'elenco." +
                           languageInstruction;
        var userMessage = TrimToMaxChars(text, MaxUserMessageChars / 2);

        var summary = await CallChatCompletionsAsync(systemPrompt, userMessage, ct);
        return TrimToMaxChars(summary, MaxSummaryChars * 2);
    }

    private static IEnumerable<string> SplitDiffIntoChunks(string diff, int chunkSize)
    {
        var chunks = new List<string>();
        var builder = new StringBuilder();

        using var reader = new StringReader(diff);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (builder.Length + line.Length + Environment.NewLine.Length > chunkSize && builder.Length > 0)
            {
                chunks.Add(builder.ToString());
                builder.Clear();
            }

            builder.AppendLine(line);
        }

        if (builder.Length > 0)
            chunks.Add(builder.ToString());

        return chunks;
    }

    private static string BuildFileListContext(string diff, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(diff) || maxChars <= 0)
            return string.Empty;

        var files = ExtractFilePaths(diff);
        if (files.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine($"File modificati ({files.Count}):");

        foreach (var file in files)
        {
            var line = $"- {file}";
            if (builder.Length + line.Length + Environment.NewLine.Length > maxChars)
            {
                builder.AppendLine("... (lista troncata)");
                break;
            }

            builder.AppendLine(line);
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<string> ExtractFilePaths(string diff)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StringReader(diff);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith("Index: ", StringComparison.Ordinal))
            {
                var path = line["Index: ".Length..].Trim();
                AddPath(path);
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal) || line.StartsWith("--- ", StringComparison.Ordinal))
            {
                var path = line[4..].Trim();
                if (path.StartsWith("/dev/null", StringComparison.OrdinalIgnoreCase))
                    continue;

                var spaceIndex = path.IndexOf(" (", StringComparison.Ordinal);
                if (spaceIndex > 0)
                    path = path[..spaceIndex];

                AddPath(path);
            }
        }

        return results;

        void AddPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            if (seen.Add(path))
                results.Add(path);
        }
    }

    private static string BuildImproveUserMessage(string draft, string diffContext)
    {
        var prefix = $"Messaggio originale: {draft}\n\nDiff di riferimento:\n";
        var remaining = MaxUserMessageChars - prefix.Length;
        if (remaining <= 0)
            return TrimToMaxChars(prefix, MaxUserMessageChars);

        diffContext = TrimToMaxChars(diffContext, remaining);
        return prefix + diffContext;
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
        => await CallChatCompletionsAsync(systemPrompt, userMessage, ct, allowRetryOnLimit: true);

    private async Task<string> CallChatCompletionsAsync(
        string systemPrompt,
        string userMessage,
        CancellationToken ct,
        bool allowRetryOnLimit)
    {
        userMessage = TrimToMaxChars(userMessage, MaxUserMessageChars);

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

        // Costruiamo una HttpRequestMessage per impostare l'header Authorization
        // sulla singola richiesta invece che sui DefaultRequestHeaders del client
        // condiviso: questo evita race condition quando più operazioni AI girano
        // in parallelo sullo stesso HttpClient singleton.
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
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

            throw new HttpRequestException(
                $"AI API error {(int)response.StatusCode} {response.StatusCode}: {responseJson}");
        }

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

    private static string TrimToMaxChars(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0)
            return string.Empty;

        if (text.Length <= maxChars)
            return text;

        return text[..maxChars] + "\n...(troncato)";
    }

    private static bool IsTokenLimitError(HttpStatusCode statusCode, string responseJson)
    {
        if (statusCode == HttpStatusCode.RequestEntityTooLarge)
            return true;

        return responseJson.Contains("tokens_limit_reached", StringComparison.OrdinalIgnoreCase)
               || responseJson.Contains("Request body too large", StringComparison.OrdinalIgnoreCase);
    }
}
