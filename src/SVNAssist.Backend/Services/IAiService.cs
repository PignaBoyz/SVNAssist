namespace SVNAssist.Services;

/// <summary>Provider che dichiara il modello usato davvero per l'ultima risposta (es. Copilot con il modello predefinito): la UI lo mostra.</summary>
internal interface IReportsModel
{
    string? LastModelUsed { get; }

    /// <summary>Consumo dell'ultima generazione (somma di tutte le richieste), solo se il provider lo riporta.</summary>
    AiUsage? LastUsage => null;
}

/// <summary>
/// Consumo riportato dal provider: modello reale, token e (solo se lo dice il provider, oggi la CLI di Claude Code) costo in dollari.
/// Mai stimato con prezzi scritti a mano.
/// </summary>
public sealed record AiUsage(string? Model, long InputTokens, long OutputTokens, decimal? CostUsd = null)
{
    public AiUsage Plus(AiUsage o) => new(Model ?? o.Model, InputTokens + o.InputTokens, OutputTokens + o.OutputTokens,
        CostUsd is null && o.CostUsd is null ? null : (CostUsd ?? 0) + (o.CostUsd ?? 0));
}

/// <summary>
/// Interfaccia per la generazione di commit message tramite AI.
/// L'implementazione concreta (<see cref="AiService"/>) chiama un endpoint
/// compatibile OpenAI Chat Completions (es. OpenAI, Azure OpenAI, LM Studio, Ollama).
/// </summary>
public interface IAiService
{
    /// <summary>
    /// Genera un messaggio di commit analizzando il diff SVN fornito.
    /// </summary>
    /// <param name="diff">Output del diff SVN (testo unified diff).</param>
    /// <param name="language">
    /// Lingua per il messaggio: <c>"italiano"</c>, <c>"english"</c> o <c>"auto"</c>.
    /// Con <c>"auto"</c> il modello sceglie liberamente la lingua.
    /// </param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    /// <param name="projectInstructions">Regole di commit del progetto/utente (es. copilot-instructions.md), aggiunte al prompt di sistema.</param>
    /// <returns>Il messaggio di commit generato dal modello AI.</returns>
    Task<string> GenerateCommitMessageAsync(string diff, string language, CancellationToken ct, string? projectInstructions = null);

    /// <summary>
    /// Migliora un messaggio di commit esistente rendendolo più chiaro e professionale.
    /// </summary>
    /// <param name="draft">Il messaggio di commit da migliorare.</param>
    /// <param name="diff">Il diff SVN di riferimento per contesto.</param>
    /// <param name="language">
    /// Lingua per il messaggio: <c>"italiano"</c>, <c>"english"</c> o <c>"auto"</c>.
    /// </param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    /// <param name="style"><c>"shorter"</c> o <c>"detailed"</c> per accorciare/dettagliare il messaggio corrente; <c>null</c> = solo migliorare.</param>
    /// <returns>Il messaggio di commit migliorato.</returns>
    Task<string> ImproveCommitMessageAsync(string draft, string diff, string language, CancellationToken ct, string? projectInstructions = null, string? style = null);

    /// <summary>Riassunto a parole semplici delle revisioni in arrivo con SVN Update (solo log e nomi file, mai contenuto): intento e rischi, breve.</summary>
    Task<string> SummarizeIncomingAsync(string logText, string language, CancellationToken ct) => throw new NotSupportedException();
}
