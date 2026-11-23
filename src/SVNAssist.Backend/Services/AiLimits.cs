namespace SVNAssist.Services;

/// <summary>
/// Limiti di un provider AI, dichiarati in un punto solo (debito D16). Valori prudenti: ~4 caratteri per token.
/// </summary>
/// <param name="MaxUserChars">Messaggio utente per singola richiesta; oltre, il diff si riassume a blocchi.</param>
/// <param name="MaxResponseTokens">Token massimi della risposta (<c>max_tokens</c>) dove il provider lo accetta.</param>
/// <param name="DiffMaxTotalChars">Diff totale che <see cref="DiffLimiter"/> lascia uscire verso il provider.</param>
/// <param name="DiffMaxFileChars">Diff massimo per singolo file.</param>
public sealed record AiLimits(int MaxUserChars, int MaxResponseTokens, int DiffMaxTotalChars, int DiffMaxFileChars)
{
    /// <summary>GitHub Models: ~8000 token per richiesta (7000 utili per il messaggio utente).</summary>
    public static readonly AiLimits GitHubModels = new(28_000, 500, 120_000, 20_000);

    /// <summary>Endpoint OpenAI-compatibili generici (OpenAI, Azure, Ollama, LM Studio): contesto ignoto, come il più stretto.</summary>
    public static readonly AiLimits OpenAiCompatible = new(28_000, 500, 120_000, 20_000);

    /// <summary>Claude API: contesto ampio; margine per il ragionamento prima del testo.</summary>
    public static readonly AiLimits Anthropic = new(100_000, 16_000, 300_000, 40_000);

    /// <summary>Claude Code (CLI): il limite di risposta lo gestisce la CLI.</summary>
    public static readonly AiLimits ClaudeCode = new(100_000, 0, 300_000, 40_000);

    /// <summary>Copilot: prompt oltre ~60k caratteri danno "No response was returned"; risposta gestita dal runtime.</summary>
    public static readonly AiLimits Copilot = new(60_000, 0, 180_000, 30_000);

    /// <summary>Gemini (Google AI Studio): contesto ampio; margine token per modelli con ragionamento (2.5/3).</summary>
    public static readonly AiLimits Gemini = new(100_000, 8_192, 300_000, 40_000);
}
