using System.Globalization;
using System.Text.RegularExpressions;

namespace SVNAssist.Models;

/// <summary>Una riga d'azione di <c>svn merge</c>: colonne file (U A D C G E R), proprieta' (U C G), albero (C).</summary>
public sealed record MergeItem(char File, char Prop, bool TreeConflict, string Path)
{
    public bool Conflicted => File == 'C' || Prop == 'C' || TreeConflict;

    /// <summary>Azione per la UI: C se in conflitto, altrimenti A / D / R, il resto (U, G, solo proprieta') e' una modifica (M).</summary>
    public string Action => Conflicted ? "C" : File is 'A' or 'D' or 'R' ? File.ToString() : "M";
}

/// <summary>Esito (anche di <c>--dry-run</c>) di <c>svn merge</c>: righe d'azione e conteggi dei conflitti.</summary>
/// <param name="Conflicts">Elementi in conflitto (testo, proprieta' o albero): dal riepilogo "Summary of conflicts" se c'e', altrimenti dalle righe.</param>
/// <param name="Skipped">Percorsi saltati (<c>Skipped 'x'</c>, <c>Skipped missing target</c>, riepilogo).</param>
public sealed record MergeOutput(IReadOnlyList<MergeItem> Items, int Conflicts, int Skipped);

/// <summary>Parser dell'output testuale (inglese, <c>LC_ALL=C</c>) di <c>svn mergeinfo</c> e <c>svn merge</c>: non esiste <c>--xml</c>.</summary>
public static partial class MergeParser
{
    /// <summary><c>svn mergeinfo --show-revs eligible</c>: una <c>rNNN</c> per riga (le revisioni non operative hanno un <c>*</c>). Ordine crescente, senza doppioni.</summary>
    public static IReadOnlyList<long> ParseRevisions(string? output)
        => RevLine().Matches(output ?? string.Empty)
            .Select(m => long.Parse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture))
            .Distinct().Order().ToList();

    /// <summary>
    /// Righe d'azione: 4 colonne di stato + spazio + percorso (<c>U    a.c</c>, <c> U   dir</c> proprieta', <c>   C x</c> conflitto ad albero),
    /// ignorando le intestazioni <c>--- Merging r1 through r3 into '.':</c> e <c>--- Recording mergeinfo ...</c>.
    /// </summary>
    public static MergeOutput ParseMerge(string? output)
    {
        var items = new List<MergeItem>();
        int? sumConflicts = null, sumSkipped = null;
        var skipped = 0;
        var bookkeeping = false; // dopo "--- Recording/Eliding mergeinfo" le righe sono la proprieta' svn:mergeinfo (' U   .'), non file del merge
        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                bookkeeping = line.StartsWith("--- Recording", StringComparison.Ordinal) || line.StartsWith("--- Eliding", StringComparison.Ordinal);
                continue;
            }

            var a = ActionLine().Match(line);
            if (bookkeeping && a.Success)
                continue;
            if (a.Success && a.Groups["cols"].Value.Trim().Length > 0)
            {
                var c = a.Groups["cols"].Value;
                items.Add(new MergeItem(c[0], c[1], c[3] == 'C', a.Groups["path"].Value));
                continue;
            }

            var s = Summary().Match(line);
            if (s.Success)
            {
                var n = int.Parse(s.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
                if (s.Groups["k"].Value == "Skipped")
                    sumSkipped = n;
                else
                    sumConflicts = (sumConflicts ?? 0) + n;
            }
            else if (SkippedLine().IsMatch(line))
            {
                skipped++;
            }
        }

        return new MergeOutput(items, sumConflicts ?? items.Count(i => i.Conflicted), Math.Max(skipped, sumSkipped ?? 0));
    }

    [GeneratedRegex(@"^\s*r(\d+)\*?\s*$", RegexOptions.Multiline)]
    private static partial Regex RevLine();

    [GeneratedRegex(@"^(?<cols>[ ADUCGER][ UCG][ B][ C]) (?<path>\S.*?)\s*$")]
    private static partial Regex ActionLine();

    [GeneratedRegex(@"^\s+(?<k>Text|Property|Tree|Skipped) (?:conflicts|paths): (?<n>\d+)\s*$")]
    private static partial Regex Summary();

    [GeneratedRegex(@"^Skipped (?:missing target: )?'")]
    private static partial Regex SkippedLine();
}
