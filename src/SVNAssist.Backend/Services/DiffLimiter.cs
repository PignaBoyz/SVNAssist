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

    /// <summary>Riduce <paramref name="diff"/> ai limiti per file e totale, segnalando i tagli nel testo.</summary>
    public static Result Limit(string diff, int maxTotalChars = MaxTotalChars, int maxFileChars = MaxFileChars)
    {
        if (string.IsNullOrEmpty(diff) || diff.Length <= Math.Min(maxFileChars, maxTotalChars))
            return new Result(diff, false, 0);

        var builder = new StringBuilder();
        var truncated = false;
        var omitted = 0;

        foreach (var section in SplitByFile(diff))
        {
            if (builder.Length >= maxTotalChars)
            {
                omitted++;
                truncated = true;
                continue;
            }

            var text = section;
            var limit = Math.Min(maxFileChars, maxTotalChars - builder.Length);
            if (text.Length > limit)
            {
                text = text[..limit].TrimEnd() + "\n" + TruncatedMarker + "\n";
                truncated = true;
            }

            if (builder.Length > 0 && builder[^1] != '\n')
                builder.Append('\n');
            builder.Append(text);
        }

        if (omitted > 0)
            builder.Append($"\n... (altri {omitted} file omessi)\n");

        return new Result(builder.ToString(), truncated, omitted);
    }

    /// <summary>Spezza il diff in sezioni che iniziano con <c>Index: </c> (il testo prima della prima è una sezione a sé).</summary>
    private static IEnumerable<string> SplitByFile(string diff)
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
