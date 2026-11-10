using System.IO;
using System.Text;

namespace SVNAssist.Services;

/// <summary>
/// Logica comune a tutti i provider AI: costruzione dei prompt, lista dei file modificati
/// e riassunto a blocchi dei diff troppo grandi.
/// </summary>
/// <remarks>
/// Ogni provider (OpenAI-compatibile, Claude API, Claude Code, GitHub Copilot) implementa
/// solo <see cref="CompleteAsync"/>: "dato un prompt di sistema e un messaggio utente,
/// restituisci il testo della risposta". Tutto il resto vive qui, una volta sola.
/// </remarks>
public abstract class AiServiceBase : IAiService
{
    private const int ApproxCharsPerToken = 4;
    private const int MaxRequestTokens = 8000;
    private const int MaxResponseTokens = 200;
    private const int SafetyPromptTokens = 800;
    private const int MaxUserTokens = MaxRequestTokens - MaxResponseTokens - SafetyPromptTokens;
    private const int DiffChunkSizeChars = 12000;
    private const int MaxSummaryChars = 1200;
    private const int MaxCombinedSummaryChars = 8000;
    private const int MaxFileListChars = 6000;
    private const int MinDiffContextChars = 2000;

    /// <summary>
    /// Limite del messaggio utente. Tarato sul limite più stretto (GitHub Models, ~8000 token
    /// per richiesta) così lo stesso prompt funziona con tutti i provider.
    /// </summary>
    protected const int MaxUserMessageChars = MaxUserTokens * ApproxCharsPerToken;

    /// <summary>
    /// Invia una singola richiesta al modello e restituisce il testo della risposta.
    /// </summary>
    /// <param name="systemPrompt">Istruzioni di sistema.</param>
    /// <param name="userMessage">Messaggio utente, già entro <see cref="MaxUserMessageChars"/>.</param>
    /// <param name="ct">Token di cancellazione.</param>
    protected abstract Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct);

    /// <inheritdoc />
    public async Task<string> GenerateCommitMessageAsync(string diff, string language, CancellationToken ct)
    {
        var systemPrompt = "Sei un assistente per sviluppatori. Analizza il seguente diff SVN " +
                           "e genera un messaggio di commit chiaro e conciso. " +
                           "Rispondi solo con il messaggio, niente altro." +
                           GetLanguageInstruction(language);

        var diffContext = await GetDiffContextAsync(diff, language, ct);
        return await AskAsync(systemPrompt, diffContext, ct);
    }

    /// <inheritdoc />
    public async Task<string> ImproveCommitMessageAsync(string draft, string diff, string language, CancellationToken ct)
    {
        var systemPrompt = "Sei un assistente per sviluppatori. Migliora questo messaggio di commit " +
                           "rendendolo più chiaro e professionale. " +
                           "Rispondi solo con il messaggio migliorato, niente altro." +
                           GetLanguageInstruction(language);

        var diffContext = await GetDiffContextAsync(diff, language, ct);
        return await AskAsync(systemPrompt, BuildImproveUserMessage(draft, diffContext), ct);
    }

    private async Task<string> AskAsync(string systemPrompt, string userMessage, CancellationToken ct)
    {
        var answer = await CompleteAsync(systemPrompt, TrimToMaxChars(userMessage, MaxUserMessageChars), ct);
        return answer.Trim();
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

            var summary = await AskAsync(systemPrompt, userMessage, ct);
            summaries.Add(TrimToMaxChars(summary, MaxSummaryChars));
        }

        var combined = string.Join("\n", summaries);
        if (combined.Length > MaxCombinedSummaryChars)
            combined = await SummarizeTextAsync(combined, language, ct);

        return TrimToMaxChars(combined, MaxCombinedSummaryChars);
    }

    private async Task<string> SummarizeTextAsync(string text, string language, CancellationToken ct)
    {
        var systemPrompt = "Sei un assistente per sviluppatori. Riassumi il testo seguente " +
                           "in un elenco puntato chiaro e tecnico (max 8 punti). " +
                           "Rispondi solo con l'elenco." +
                           GetLanguageInstruction(language);

        var summary = await AskAsync(systemPrompt, TrimToMaxChars(text, MaxUserMessageChars / 2), ct);
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

    internal static IReadOnlyList<string> ExtractFilePaths(string diff)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StringReader(diff);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith("Index: ", StringComparison.Ordinal))
            {
                AddPath(line["Index: ".Length..].Trim());
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal) || line.StartsWith("--- ", StringComparison.Ordinal))
            {
                var path = line[4..];
                if (path.StartsWith("/dev/null", StringComparison.OrdinalIgnoreCase))
                    continue;

                // svn diff separa path e revisione con un TAB ("a.cs\t(revision 5)");
                // altri tool usano uno spazio ("a.cs (working copy)").
                var cut = path.IndexOf('\t');
                if (cut < 0)
                    cut = path.IndexOf(" (", StringComparison.Ordinal);
                if (cut > 0)
                    path = path[..cut];

                AddPath(path.Trim());
            }
        }

        return results;

        void AddPath(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
                results.Add(path);
        }
    }

    private static string BuildImproveUserMessage(string draft, string diffContext)
    {
        var prefix = $"Messaggio originale: {draft}\n\nDiff di riferimento:\n";
        var remaining = MaxUserMessageChars - prefix.Length;
        if (remaining <= 0)
            return TrimToMaxChars(prefix, MaxUserMessageChars);

        return prefix + TrimToMaxChars(diffContext, remaining);
    }

    /// <summary>
    /// Istruzione sulla lingua da aggiungere al prompt.
    /// Con <c>"auto"</c> non si specifica nulla: il modello sceglie la lingua del diff.
    /// </summary>
    private static string GetLanguageInstruction(string language) =>
        string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase) ? string.Empty : $" Lingua: {language}.";

    /// <summary>Tronca il testo a <paramref name="maxChars"/> caratteri aggiungendo un marcatore.</summary>
    protected static string TrimToMaxChars(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0)
            return string.Empty;

        if (text.Length <= maxChars)
            return text;

        return text[..maxChars] + "\n...(troncato)";
    }
}
