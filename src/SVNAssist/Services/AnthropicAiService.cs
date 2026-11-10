using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace SVNAssist.Services;

/// <summary>
/// Provider AI che chiama la <b>Claude API</b> di Anthropic con l'SDK ufficiale (<c>Anthropic</c>).
/// Richiede una API key Anthropic (impostazione o variabile <c>ANTHROPIC_API_KEY</c>).
/// </summary>
internal sealed class AnthropicAiService : AiServiceBase
{
    // I modelli per cui è disponibile il fallback lato server in caso di rifiuto.
    private static readonly string[] FallbackModels =
        ["claude-opus-5-5", "claude-opus-5", "claude-fable-5-1", "claude-sonnet-5-5"];

    private readonly AnthropicClient _client;
    private readonly string _model;

    /// <param name="apiKey">API key; se vuota l'SDK usa le credenziali di ambiente.</param>
    /// <param name="model">Modello Claude (default <c>claude-opus-5-5</c>).</param>
    public AnthropicAiService(string? apiKey, string model)
    {
        _client = string.IsNullOrWhiteSpace(apiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = apiKey };
        _model = model;
    }

    /// <inheritdoc />
    protected override async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct)
    {
        var useFallback = FallbackModels.Contains(_model, StringComparer.OrdinalIgnoreCase);

        var response = await _client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = _model,
            // Margine ampio: sui modelli recenti il ragionamento consuma token prima del testo.
            MaxTokens = 16000,
            // Un commit message non richiede ragionamento profondo.
            OutputConfig = new() { Effort = Effort.Low },
            System = systemPrompt,
            Messages = [new BetaUserMessageParam { Content = userMessage }],
            // Se il modello rifiuta per un falso positivo dei filtri, il server riprova con un altro modello.
            Betas = useFallback ? ["server-side-fallback-2026-07-01"] : null,
            // "default": il server sceglie il modello di riserva in base al tipo di rifiuto.
            Fallbacks = useFallback ? (BetaFallbacksParam)new Default() : null,
        }, ct);

        if (response.StopReason == "refusal")
            throw new InvalidOperationException("Claude ha rifiutato la richiesta.");

        var text = string.Concat(response.Content
            .Select(b => b.TryPickText(out var t) ? t.Text : null)
            .Where(t => t is not null));

        return string.IsNullOrWhiteSpace(text)
            ? throw new InvalidOperationException("Claude non ha restituito testo.")
            : text;
    }
}
