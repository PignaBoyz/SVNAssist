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
    private const int MaxFileListChars = 6000;
    private const int MinDiffContextChars = 2000;

    /// <summary>Budget del messaggio utente per i provider con contesto ampio (~25k token).</summary>
    protected const int LargeContextUserMessageChars = 100_000;

    /// <summary>
    /// Limite del messaggio utente in caratteri. Il default è tarato sul limite più stretto
    /// (GitHub Models, ~8000 token per richiesta); i provider con contesto ampio lo alzano.
    /// Un diff che sta nel budget viene inviato intero, altrimenti viene riassunto a blocchi.
    /// </summary>
    protected virtual int MaxUserMessageChars => MaxUserTokens * ApproxCharsPerToken;

    /// <summary>
    /// Invia una singola richiesta al modello e restituisce il testo della risposta.
    /// </summary>
    /// <param name="systemPrompt">Istruzioni di sistema.</param>
    /// <param name="userMessage">Messaggio utente, già entro <see cref="MaxUserMessageChars"/>.</param>
    /// <param name="ct">Token di cancellazione.</param>
    protected abstract Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct);

    /// <inheritdoc />
    public async Task<string> GenerateCommitMessageAsync(string diff, string language, CancellationToken ct, string? projectInstructions = null)
    {
        var systemPrompt = "Sei un assistente per sviluppatori. Analizza il seguente diff SVN " +
                           "e genera un messaggio di commit completo nello stile dei buoni commit Git: " +
                           "una riga di titolo (max 72 caratteri), riga vuota, poi un elenco puntato per area o modulo con cosa è cambiato e perché; " +
                           "per modifiche piccole bastano il titolo e poche righe. " +
                           "Se il diff arriva come riassunti per blocco, usali tutti. In un merge cita le revisioni unite se note. " +
                           "Elenca nel messaggio anche i file aggiunti (A) o eliminati (D), non solo quelli modificati. " +
                           "Rispondi solo con il messaggio, niente altro." +
                           GetLanguageInstruction(language) +
                           CommitInstructions.ForPrompt(projectInstructions);

        var diffContext = await GetDiffContextAsync(diff, language, ct);
        return await AskAsync(systemPrompt, diffContext, ct);
    }

    /// <inheritdoc />
    public async Task<string> ImproveCommitMessageAsync(string draft, string diff, string language, CancellationToken ct, string? projectInstructions = null, string? style = null)
    {
        var systemPrompt = "Sei un assistente per sviluppatori. Migliora questo messaggio di commit " +
                           "rendendolo più chiaro e professionale. " +
                           "Rispondi solo con il messaggio migliorato, niente altro." +
                           StyleInstruction(style) +
                           GetLanguageInstruction(language) +
                           CommitInstructions.ForPrompt(projectInstructions);

        var diffContext = await GetDiffContextAsync(diff, language, ct);
        return await AskAsync(systemPrompt, BuildImproveUserMessage(draft, diffContext), ct);
    }

    /// <summary>Istruzione di stile per "Più corto" (<c>shorter</c>) e "Più dettagliato" (<c>detailed</c>); altri valori: nessuna.</summary>
    internal static string StyleInstruction(string? style) => style switch
    {
        "shorter" => " Accorcia il messaggio: riassumilo in poche righe tenendo solo l'essenziale.",
        "detailed" => " Rendi il messaggio più dettagliato: aggiungi contesto e l'elenco puntato delle modifiche principali, senza inventare nulla che non sia nel diff.",
        _ => string.Empty,
    };

    /// <summary>Tempo massimo per una singola richiesta al provider AI.</summary>
    internal virtual TimeSpan CallTimeout => TimeSpan.FromMinutes(3);

    private async Task<string> AskAsync(string systemPrompt, string userMessage, CancellationToken ct)
    {
        // Timeout collegato all'annullamento dell'utente ma distinguibile: un provider appeso non blocca per sempre.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(CallTimeout);
        try
        {
            var answer = CleanAnswer(await CompleteAsync(systemPrompt, TrimToMaxChars(userMessage, MaxUserMessageChars), timeoutCts.Token));
            // Una risposta vuota non è un successo: senza errore l'utente vedrebbe "generato" ma nessun testo.
            if (string.IsNullOrEmpty(answer))
                throw new InvalidOperationException(Loc.T("Il provider AI ha risposto con un messaggio vuoto (modello sovraccarico o filtro di contenuto): riprova."));
            return answer;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                Loc.T("Il provider AI non ha risposto entro {0:0.#} minuti: richiesta interrotta.", CallTimeout.TotalMinutes));
        }
    }

    /// <summary>
    /// Toglie dalla risposta AI l'involucro che non fa parte del messaggio: blocco di codice markdown (``` con linguaggio facoltativo,
    /// anche non chiuso) e virgolette che avvolgono tutto il testo. Un solo punto, usato anche per i messaggi recenti gia' salvati.
    /// </summary>
    public static string CleanAnswer(string? text)
    {
        var t = (text ?? "").Trim();
        for (var i = 0; i < 3; i++) // al piu' qualche involucro annidato (virgolette dentro il blocco, ecc.)
        {
            var before = t;
            if (t.StartsWith("```", StringComparison.Ordinal))
            {
                var nl = t.IndexOf('\n');
                t = nl < 0 ? "" : t[(nl + 1)..].TrimEnd(); // salta la riga di apertura ("```" o "```text")
                if (t.EndsWith("```", StringComparison.Ordinal))
                    t = t[..^3];
                t = t.Trim();
            }
            else if (t.EndsWith("```", StringComparison.Ordinal) && !t[..^3].Contains("```"))
                t = t[..^3].Trim(); // chiusura rimasta senza apertura
            if (t.Length >= 2 && ((t[0] == '"' && t[^1] == '"' && !t[1..^1].Contains('"')) || (t[0] == '“' && t[^1] == '”')))
                t = t[1..^1].Trim();
            if (t == before)
                break;
        }
        return t;
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

        // Diff che sta in una richiesta: una sola chiamata. Altrimenti map-reduce su TUTTO il diff (nessun file escluso).
        var diffContext = diff.Length <= diffBudgetChars
            ? diff
            : await MapReduceDiffAsync(diff, language, diffBudgetChars, ct);

        diffContext = TrimToMaxChars(diffContext, diffBudgetChars);

        if (string.IsNullOrWhiteSpace(fileListContext))
            return diffContext;

        return $"{fileListContext}\n\n{diffContext}";
    }

    private const int MaxMapFileChars = 100_000;

    /// <summary>Richieste "map" in parallelo.</summary>
    internal const int MapConcurrency = 3;

    /// <summary>Notifica l'avanzamento (es. "Analisi blocco 3/12...") a chi mostra lo stato; impostato dal chiamante.</summary>
    internal Action<string>? Progress { get; set; }

    /// <summary>Dimensione di un blocco del diff: meta' del budget del modello (prudente per i modelli ridotti), tra 4k e 24k caratteri.</summary>
    internal int ChunkChars => Math.Clamp(MaxUserMessageChars / 2, 4000, DiffChunkSizeChars * 2);

    /// <summary>
    /// Map: ogni blocco del diff (file interi o pezzi per hunk) diventa un riassunto fattuale. Reduce: i riassunti
    /// (a piu' livelli se servono) entrano nel budget e il messaggio finale li usa tutti.
    /// </summary>
    private async Task<string> MapReduceDiffAsync(string diff, string language, int budgetChars, CancellationToken ct)
    {
        var (textDiff, propLines, merged) = DiffLimiter.CollapsePropertyOnly(diff);
        var propsBlock = BuildPropsBlock(propLines, merged);
        // shortcut: un singolo file oltre 100k caratteri (es. SQL/dati aggiunti per errore) si analizza solo nell'inizio; alzare se serve.
        textDiff = string.Concat(DiffLimiter.SplitByFile(textDiff).Select(s => s.Length > MaxMapFileChars
            ? s[..MaxMapFileChars] + "\n... (file molto grande: analizzato solo l'inizio)\n" : s));
        var chunks = ChunkDiff(textDiff, ChunkChars);
        var summaries = new string[chunks.Count];
        var languageInstruction = GetLanguageInstruction(language);
        var started = 0;

        using var gate = new SemaphoreSlim(MapConcurrency);
        using var failure = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Task.WhenAll(chunks.Select(async (chunk, i) =>
        {
            await gate.WaitAsync(failure.Token);
            try
            {
                Progress?.Invoke(Loc.T("Analisi blocco {0}/{1}...", Interlocked.Increment(ref started), chunks.Count));
                var systemPrompt = "Sei un assistente per sviluppatori. Riassumi in modo fattuale e conciso le modifiche di questo " +
                                   "blocco di diff SVN: cosa cambia e perché, citando nomi di classi, metodi e file. " +
                                   "Elenco puntato (max 8 punti, max 1200 caratteri), solo l'elenco." + languageInstruction;
                var userMessage = $"Blocco {i + 1}/{chunks.Count}:\n{chunk}";
                string summary;
                try
                {
                    summary = await AskAsync(systemPrompt, userMessage, failure.Token);
                }
                catch (InvalidOperationException) when (!failure.IsCancellationRequested)
                {
                    summary = await AskAsync(systemPrompt, userMessage, failure.Token); // un nuovo tentativo per risposta vuota
                }

                summaries[i] = $"[{i + 1}/{chunks.Count}] " + TrimToMaxChars(summary, MaxSummaryChars);
            }
            catch
            {
                failure.Cancel();
                throw;
            }
            finally
            {
                gate.Release();
            }
        }));

        var reduceBudget = Math.Max(1000, budgetChars - propsBlock.Length - 100);
        var level = summaries.ToList();
        for (var round = 0; round < 6 && level.Sum(s => s.Length + 1) > reduceBudget; round++)
        {
            // Raggruppa riassunti consecutivi in lotti che stanno nel budget e li riassume a loro volta.
            var batches = new List<string>();
            var current = new StringBuilder();
            foreach (var s in level)
            {
                if (current.Length > 0 && current.Length + s.Length + 1 > reduceBudget * 4 / 5)
                {
                    batches.Add(current.ToString());
                    current.Clear();
                }

                current.AppendLine(s);
            }

            if (current.Length > 0)
                batches.Add(current.ToString());
            if (batches.Count >= level.Count)
                break; // nessun progresso possibile: si tronca sotto

            var reduced = new List<string>();
            for (var b = 0; b < batches.Count; b++)
            {
                Progress?.Invoke(Loc.T("Riduzione riassunti {0}/{1}...", b + 1, batches.Count));
                reduced.Add(await SummarizeTextAsync(batches[b], language, ct));
            }

            level = reduced;
        }

        Progress?.Invoke(Loc.T("Composizione del messaggio finale..."));
        var body = TrimToMaxChars(string.Join("\n", level), reduceBudget);
        var header = $"Riassunti per blocco di tutto il diff ({chunks.Count} blocchi, nessun file escluso):\n";
        return header + body + (propsBlock.Length > 0 ? "\n\n" + propsBlock : string.Empty);
    }

    private static string BuildPropsBlock(List<string> propLines, List<string> merged)
    {
        if (propLines.Count == 0)
            return string.Empty;

        var sb = new StringBuilder($"{propLines.Count} file con sole modifiche di proprietà (nessuna riga di testo cambiata)");
        if (merged.Count > 0)
            sb.Append(". Revisioni unite (svn:mergeinfo): ").Append(string.Join("; ", merged));
        return TrimToMaxChars(sb.ToString(), 1500);
    }

    private async Task<string> SummarizeTextAsync(string text, string language, CancellationToken ct)
    {
        var systemPrompt = "Sei un assistente per sviluppatori. Riassumi i riassunti seguenti, senza perdere nomi di classi, metodi e aree toccate, " +
                           "in un elenco puntato chiaro e tecnico (max 10 punti). " +
                           "Rispondi solo con l'elenco." +
                           GetLanguageInstruction(language);

        var summary = await AskAsync(systemPrompt, TrimToMaxChars(text, MaxUserMessageChars / 2), ct);
        return TrimToMaxChars(summary, MaxSummaryChars * 2);
    }

    /// <summary>
    /// Divide TUTTO il diff in blocchi di al massimo <paramref name="size"/> caratteri: file interi raggruppati; un file
    /// piu' grande si spezza per hunk (e, se un hunk e' enorme, per righe) ripetendo l'intestazione "Index:" nel blocco.
    /// Ogni carattere del diff finisce in almeno un blocco.
    /// </summary>
    internal static List<string> ChunkDiff(string diff, int size)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0)
                chunks.Add(current.ToString());
            current.Clear();
        }

        void Add(string piece, string label)
        {
            if (current.Length + piece.Length > size)
                Flush();
            if (current.Length == 0 && !piece.StartsWith("Index: ", StringComparison.Ordinal))
                current.Append(label);
            current.Append(piece);
        }

        foreach (var section in DiffLimiter.SplitByFile(diff))
        {
            if (current.Length + section.Length <= size)
            {
                current.Append(section);
                continue;
            }

            if (section.Length <= size)
            {
                Flush();
                current.Append(section);
                continue;
            }

            // File grande: pezzi per hunk, ognuno non oltre size - etichetta.
            var first = section.Split('\n', 2)[0].TrimEnd('\r');
            var label = (first.Length > 200 ? first[..200] : first) + " (continua)\n";
            var limit = Math.Max(500, size - label.Length);
            Flush();
            foreach (var hunk in SplitHunks(section))
                for (var pos = 0; pos < hunk.Length;)
                {
                    var take = Math.Min(limit, hunk.Length - pos);
                    if (take == limit)
                    {
                        var nl = hunk.LastIndexOf('\n', pos + take - 1, take); // spezza a fine riga se possibile
                        if (nl > pos)
                            take = nl - pos + 1;
                    }

                    Add(hunk.Substring(pos, take), label);
                    pos += take;
                }
        }

        Flush();
        return chunks;
    }

    private static IEnumerable<string> SplitHunks(string section)
    {
        var start = 0;
        var pos = 0;
        while (pos < section.Length)
        {
            var next = section.IndexOf('\n', pos);
            var end = next < 0 ? section.Length : next + 1;
            if (pos > start && section[pos] == '@' && string.CompareOrdinal(section, pos, "@@", 0, 2) == 0)
            {
                yield return section[start..pos];
                start = pos;
            }

            pos = end;
        }

        if (start < section.Length)
            yield return section[start..];
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

    private string BuildImproveUserMessage(string draft, string diffContext)
    {
        var prefix = $"Messaggio originale: {draft}\n\nDiff di riferimento:\n";
        var remaining = MaxUserMessageChars - prefix.Length;
        if (remaining <= 0)
            return TrimToMaxChars(prefix, MaxUserMessageChars);

        return prefix + TrimToMaxChars(diffContext, remaining);
    }

    /// <summary>
    /// Istruzione sulla lingua da aggiungere al prompt.
    /// Con <c>"auto"</c> si chiede la lingua prevalente nel diff (i prompt sono in italiano: senza istruzione il modello risponderebbe quasi sempre in italiano).
    /// </summary>
    private static string GetLanguageInstruction(string language) =>
        string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)
            ? " Scrivi il messaggio nella lingua prevalente dei commenti e dei messaggi esistenti nel diff; se non è chiaro, usa l'inglese."
            : $" Lingua: {language}.";

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
