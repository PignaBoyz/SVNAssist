namespace SVNAssist.Services;

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
    /// <returns>Il messaggio di commit generato dal modello AI.</returns>
    Task<string> GenerateCommitMessageAsync(string diff, string language, CancellationToken ct);

    /// <summary>
    /// Migliora un messaggio di commit esistente rendendolo più chiaro e professionale.
    /// </summary>
    /// <param name="draft">Il messaggio di commit da migliorare.</param>
    /// <param name="diff">Il diff SVN di riferimento per contesto.</param>
    /// <param name="language">
    /// Lingua per il messaggio: <c>"italiano"</c>, <c>"english"</c> o <c>"auto"</c>.
    /// </param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    /// <returns>Il messaggio di commit migliorato.</returns>
    Task<string> ImproveCommitMessageAsync(string draft, string diff, string language, CancellationToken ct);
}
