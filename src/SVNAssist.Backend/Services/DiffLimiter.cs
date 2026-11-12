using System.Text;

namespace SVNAssist.Services;

/// <summary>
/// Limita la quantità di diff che esce verso un provider AI: <c>svn diff</c> stampa per intero anche i file aggiunti
/// (un file enorme spuntato per errore diventerebbe migliaia di richieste di riassunto e centinaia di KB inviati
/// a un servizio esterno).
/// </summary>
public static class DiffLimiter
{
    /// <summary>Massimo di caratteri di diff inviati in totale (~10 blocchi da 12.000 per il riassunto).</summary>
    public const int MaxTotalChars = 120_000;

    /// <summary>Massimo di caratteri per singolo file.</summary>
    public const int MaxFileChars = 20_000;

    private const string FileMarker = "Index: ";
    public const string TruncatedMarker = "... (troncato)";

    /// <summary>Risultato: il diff ridotto e se qualcosa è stato tagliato.</summary>
    public sealed record Result(string Diff, bool WasTruncated, int OmittedFiles);

    /// <summary>
    /// Riduce <paramref name="diff"/> ai limiti per file e totale, segnalando i tagli nel testo.
    /// Il budget totale e' diviso in quote eque tra i file con modifiche di testo (i piccoli lasciano spazio ai grandi),
    /// cosi' con molti file nessuno sparisce; i file con sole proprieta' (es. svn:mergeinfo) valgono una riga.
    /// </summary>
    public static Result Limit(string diff, int maxTotalChars = MaxTotalChars, int maxFileChars = MaxFileChars)
    {
        if (string.IsNullOrEmpty(diff) || diff.Length <= Math.Min(maxFileChars, maxTotalChars))
            return new Result(diff, false, 0);

        // Il testo prima del primo "Index:" (elenco file con stato) passa sempre: e' l'elenco completo dei file.
        var sections = SplitByFile(diff).ToList();
        var preamble = sections.Count > 0 && !sections[0].StartsWith(FileMarker, StringComparison.Ordinal) ? sections[0] : string.Empty;
        if (preamble.Length > 0)
            sections.RemoveAt(0);

        var props = new List<string>();
        // Seconda passata su un diff gia' ridotto: intestazione e righe-proprieta' si rigenerano, non si duplicano.
        if (preamble.Length > 0)
        {
            var kept = new List<string>();
            foreach (var line in preamble.Split('\n'))
            {
                if (line.StartsWith("[Diff inviato:", StringComparison.Ordinal))
                    continue;
                if (line.Contains(": solo proprietà (", StringComparison.Ordinal))
                    props.Add(line.TrimEnd('\r'));
                else
                    kept.Add(line);
            }

            preamble = string.Join('\n', kept);
        }

        var texts = new List<string>();
        foreach (var section in sections)
        {
            if (!section.Contains("\n@@", StringComparison.Ordinal) && section.Contains("Property changes on:", StringComparison.Ordinal))
                props.Add(PropertySummary(section));
            else
                texts.Add(section);
        }

        var remaining = Math.Max(0, maxTotalChars - preamble.Length - props.Sum(p => p.Length + 1) - 200);
        var order = texts.Select((t, i) => (t.Length, i)).OrderBy(x => x.Length).ToList();
        var share = new int[texts.Count];
        for (var k = 0; k < order.Count; k++)
        {
            var fair = Math.Max(0, remaining / (order.Count - k));
            share[order[k].i] = Math.Min(Math.Min(order[k].Length, fair), maxFileChars);
            remaining -= share[order[k].i];
        }

        var body = new StringBuilder();
        var cut = 0;
        var omitted = 0;
        for (var i = 0; i < texts.Count; i++)
        {
            if (share[i] < Math.Min(texts[i].Length, 200))
            {
                omitted++;
                continue;
            }

            var text = texts[i];
            if (text.Length > share[i])
            {
                text = text[..Math.Max(0, share[i] - TruncatedMarker.Length - 2)].TrimEnd() + "\n" + TruncatedMarker + "\n";
                cut++;
            }

            if (body.Length > 0 && body[^1] != '\n')
                body.Append('\n');
            body.Append(text);
        }

        var truncated = cut > 0 || omitted > 0;
        var head = new StringBuilder(preamble);
        if (props.Count > 0 || truncated)
        {
            if (head.Length > 0 && head[^1] != '\n')
                head.Append('\n');
            head.Append($"[Diff inviato: {texts.Count - omitted} file con modifiche di testo ({cut} tagliati), {props.Count} con sole proprietà, {omitted} omessi]\n");
            foreach (var p in props)
                head.Append(p).Append('\n');
        }

        if (head.Length > 0 && head[^1] != '\n' && body.Length > 0)
            head.Append('\n');
        if (omitted > 0)
            body.Append($"\n... (altri {omitted} file omessi)\n");

        return new Result(head.Append(body).ToString(), truncated, omitted);
    }

    /// <summary>
    /// Separa i file con sole modifiche di proprieta' (es. svn:mergeinfo dopo un merge) dal diff di testo:
    /// restituisce il diff senza di loro, una riga per file e le righe "Merged ..." distinte (max 8).
    /// </summary>
    internal static (string TextDiff, List<string> PropLines, List<string> Merged) CollapsePropertyOnly(string diff)
    {
        var text = new StringBuilder();
        var props = new List<string>();
        var merged = new List<string>();
        foreach (var section in SplitByFile(diff))
        {
            if (section.StartsWith(FileMarker, StringComparison.Ordinal)
                && !section.Contains("\n@@", StringComparison.Ordinal)
                && section.Contains("Property changes on:", StringComparison.Ordinal))
            {
                props.Add(PropertySummary(section));
                foreach (var line in section.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.StartsWith("Merged ", StringComparison.Ordinal) && merged.Count < 8 && !merged.Contains(t))
                        merged.Add(t.Length > 150 ? t[..150] + "..." : t);
                }
            }
            else
                text.Append(section);
        }

        return (text.ToString(), props, merged);
    }

    internal static string PropertySummary(string section)
    {
        var first = section.Split('\n', 2)[0].TrimEnd('\r');
        var path = first.StartsWith(FileMarker, StringComparison.Ordinal) ? first[FileMarker.Length..].Trim() : first.Trim();
        var names = new List<string>();
        foreach (var line in section.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            foreach (var kind in new[] { "Modified: ", "Added: ", "Deleted: " })
                if (t.StartsWith(kind, StringComparison.Ordinal) && !names.Contains(t[kind.Length..].Trim()))
                    names.Add(t[kind.Length..].Trim());
        }

        return $"- {path}: solo proprietà ({(names.Count > 0 ? string.Join(", ", names) : "?")})";
    }

    /// <summary>Spezza il diff in sezioni che iniziano con <c>Index: </c> (il testo prima della prima è una sezione a sé).</summary>
    internal static IEnumerable<string> SplitByFile(string diff)
    {
        var start = 0;
        var pos = 0;
        while (pos < diff.Length)
        {
            var next = diff.IndexOf('\n', pos);
            var lineEnd = next < 0 ? diff.Length : next + 1;
            if (pos > start && string.CompareOrdinal(diff, pos, FileMarker, 0, FileMarker.Length) == 0)
            {
                yield return diff[start..pos];
                start = pos;
            }

            pos = lineEnd;
        }

        if (start < diff.Length)
            yield return diff[start..];
    }
}
