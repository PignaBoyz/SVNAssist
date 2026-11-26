using System.Text.RegularExpressions;

namespace SVNAssist.Services;

/// <summary>
/// Storia di un gruppo di righe (come <c>git log -L</c>, che svn non ha): dal testo di <c>svn log --diff</c> del file si
/// risale revisione per revisione, dalla piu' recente, seguendo dove stavano le righe selezionate. Una revisione entra
/// nella storia se il suo diff tocca almeno una delle righe seguite; una riga smette di essere seguita quando nasce
/// (il diff la aggiunge senza una riga vecchia corrispondente). Classe pura (nessun I/O).
/// shortcut: in un blocco modificato la riga i-esima nuova si accoppia alla i-esima vecchia (come <see cref="LineRevert"/>); le righe in piu' sono nate li'. Upgrade se serve un allineamento per somiglianza.
/// </summary>
internal static class LineHistory
{
    /// <summary>Il diff di una revisione del file, preso da <c>svn log --diff</c>.</summary>
    internal readonly record struct RevisionDiff(long Revision, string Diff);

    /// <summary>
    /// Esito: <see cref="Revisions"/> dalla piu' recente; <see cref="LocalOnly"/> = righe selezionate che esistono solo
    /// nel file di lavoro (aggiunte e non ancora committate), quindi senza storia.
    /// </summary>
    /// <param name="Lines">Per ogni revisione trovata, le righe seguite che il suo diff tocca (numeri del lato nuovo, cioe' del file a quella revisione): servono a mostrare solo quei blocchi del diff.</param>
    internal sealed record Result(IReadOnlyList<long> Revisions, int LocalOnly, IReadOnlyDictionary<long, IReadOnlyList<int>>? Lines = null);

    private const string Separator = "------------------------------------------------------------------------";
    private static readonly Regex Header = new(@"^r(\d+) \| .*?(?: \| (\d+) lines?)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HunkHeader = new(@"^@@ -\d+(?:,(\d+))? \+\d+(?:,(\d+))? @@", RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Divide l'output testuale di <c>svn log --diff</c> (con <c>LC_ALL=C</c>) nelle singole revisioni, nell'ordine di svn (dalla piu' recente).
    /// Il messaggio si salta con il conteggio "N lines" dell'intestazione; dentro un blocco <c>@@</c> si contano le righe,
    /// cosi' una riga di codice fatta di soli trattini non viene presa per il separatore tra revisioni.
    /// </summary>
    internal static List<RevisionDiff> ParseLogDiff(string text)
    {
        var result = new List<RevisionDiff>();
        var lines = text.Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd('\r');
            var m = Header.Match(line);
            if (!m.Success || i == 0 || lines[i - 1].TrimEnd('\r') != Separator)
            {
                i++;
                continue;
            }

            var rev = long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var msgLines = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            i++; // intestazione
            if (i < lines.Length && lines[i].TrimEnd('\r').Length == 0) i++; // riga vuota prima del messaggio
            i += msgLines;

            var diff = new System.Text.StringBuilder();
            int remOld = 0, remNew = 0;
            for (; i < lines.Length; i++)
            {
                var l = lines[i].TrimEnd('\r');
                if (remOld + remNew > 0)
                {
                    char c = l.Length > 0 ? l[0] : ' ';
                    if (c == '-') remOld--;
                    else if (c == '+') remNew--;
                    else if (c != '\\') { remOld--; remNew--; }
                    diff.Append(l).Append('\n');
                    continue;
                }

                if (l == Separator)
                    break;
                var h = HunkHeader.Match(l);
                if (h.Success)
                {
                    remOld = h.Groups[1].Success ? int.Parse(h.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
                    remNew = h.Groups[2].Success ? int.Parse(h.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
                }

                diff.Append(l).Append('\n');
            }

            result.Add(new RevisionDiff(rev, diff.ToString()));
        }

        return result;
    }

    /// <summary>
    /// Dove stava la riga <paramref name="line"/> (1-based, lato nuovo del diff) prima del diff.
    /// <c>Touched</c>: il diff la aggiunge o la modifica. <c>Old</c>: la riga corrispondente nel lato vecchio, <c>null</c> se nasce qui.
    /// </summary>
    internal static (bool Touched, int? Old) MapBack(IReadOnlyList<LineRevert.Hunk> hunks, int line)
    {
        int offset = 0; // righe vecchie - righe nuove dei blocchi che stanno tutti prima della riga
        foreach (var h in hunks.OrderBy(h => h.NewStart))
        {
            if (h.NewCount > 0 && line >= h.NewStart && line < h.NewStart + h.NewCount)
            {
                var idx = line - h.NewStart;
                return (true, idx < h.OldCount ? h.OldStart + idx : null);
            }

            // Blocco tutto prima della riga (per una cancellazione pura NewStart e' la riga prima del punto tolto).
            var lastNew = h.NewCount > 0 ? h.NewStart + h.NewCount - 1 : h.NewStart;
            if (lastNew < line)
                offset += h.OldCount - h.NewCount;
        }

        return (false, line + offset);
    }

    /// <summary>
    /// Revisioni che hanno toccato le righe <paramref name="from"/>..<paramref name="to"/> (1-based, del file di lavoro).
    /// <paramref name="localDiff"/> = <c>svn diff</c> del file (lavoro rispetto a BASE, vuoto se non modificato): porta le righe sulla BASE;
    /// <paramref name="history"/> = revisioni di <c>svn log --diff</c>, dalla piu' recente.
    /// </summary>
    internal static Result Trace(string localDiff, IEnumerable<RevisionDiff> history, int from, int to)
    {
        var tracked = new SortedSet<int>();
        int localOnly = 0;
        var local = LineRevert.ParseHunks(localDiff);
        for (int line = from; line <= to; line++)
        {
            var (_, old) = MapBack(local, line);
            if (old is { } o) tracked.Add(o);
            else localOnly++;
        }

        var revisions = new List<long>();
        var lines = new Dictionary<long, IReadOnlyList<int>>();
        var list = history as IReadOnlyList<RevisionDiff> ?? history.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var rev = list[i];
            if (tracked.Count == 0)
                break;
            var hunks = LineRevert.ParseHunks(rev.Diff);
            if (hunks.Count == 0)
                continue; // revisione senza modifiche di testo al file (copia, proprieta', merge solo di mergeinfo)
            if (i < list.Count - 1 && IsWholeFileAdded(rev.Diff))
                continue;

            var next = new SortedSet<int>();
            var hit = new List<int>();
            foreach (var line in tracked)
            {
                var (t, old) = MapBack(hunks, line);
                if (t) hit.Add(line);
                if (old is { } o && o > 0) next.Add(o);
            }

            if (hit.Count > 0)
            {
                revisions.Add(rev.Revision);
                lines[rev.Revision] = hit;
            }
            tracked = next;
        }

        return new Result(revisions, localOnly, lines);
    }

    /// <summary>
    /// Diff di una revisione ridotto ai blocchi che toccano <paramref name="lines"/> (lato nuovo), con le intestazioni del file:
    /// lo Show Log delle righe apre il diff sul punto giusto invece che su tutto il file. Se nessun blocco corrisponde, il diff intero.
    /// </summary>
    internal static string FocusDiff(string diff, IReadOnlyCollection<int> lines)
    {
        var src = diff.Replace("\r\n", "\n").Split('\n');
        var head = new System.Text.StringBuilder();
        var kept = new System.Text.StringBuilder();
        System.Text.StringBuilder? hunk = null;
        bool keep = false, any = false;
        void Flush()
        {
            if (hunk is not null && keep) { kept.Append(hunk); any = true; }
            hunk = null;
        }

        foreach (var l in src)
        {
            var m = HunkHeader.Match(l);
            if (m.Success)
            {
                Flush();
                var nm = Regex.Match(l, @"\+(\d+)(?:,(\d+))?");
                int start = int.Parse(nm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                int count = nm.Groups[2].Success ? int.Parse(nm.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
                keep = lines.Any(x => x >= start && x < start + Math.Max(count, 1));
                hunk = new System.Text.StringBuilder();
                hunk.Append(l).Append('\n');
            }
            else if (hunk is not null)
            {
                if (l.StartsWith("Property changes on:", StringComparison.Ordinal) || l.StartsWith("Index: ", StringComparison.Ordinal)) { Flush(); break; }
                hunk.Append(l).Append('\n');
            }
            else
                head.Append(l).Append('\n');
        }

        Flush();
        return any ? head.ToString() + kept.ToString().TrimEnd('\n') + "\n" : diff;
    }

    /// <summary>Di un diff di piu' file (<c>svn diff -c</c> alla radice del repository) solo le sezioni del file con percorso che finisce per <paramref name="relPath"/>; il diff intero se non ce ne sono.</summary>
    internal static string OnlyFile(string diff, string relPath)
    {
        var suffix = relPath.Replace('\\', '/').TrimStart('/');
        var parts = Regex.Split(diff.Replace("\r\n", "\n"), @"(?=^Index: )", RegexOptions.Multiline);
        static string PathOf(string section)
        {
            var nl = section.IndexOf('\n');
            return section[7..(nl > 7 ? nl : section.Length)].Trim().Replace('\\', '/');
        }

        var kept = parts.Where(p => p.StartsWith("Index: ", StringComparison.Ordinal) && PathOf(p) is var path
            && (path.Equals(suffix, StringComparison.OrdinalIgnoreCase) || path.EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase))).ToList();
        return kept.Count == 0 ? diff : string.Concat(kept);
    }

    /// <summary>
    /// La creazione di un branch (<c>svn copy</c>) per svn e' una copia, ma con alcuni server (http) il diff di quella revisione
    /// mostra il file intero come aggiunto (<c>@@ -0,0 +1,N @@</c>). Se il log ha revisioni piu' vecchie il file esisteva gia':
    /// e' la copia, le righe restano dove sono e la storia continua sul ramo di origine. Senza revisioni piu' vecchie e' la nascita vera
    /// (svn log segue le copie: un file ricreato senza storia non ha revisioni prima).
    /// </summary>
    internal static bool IsWholeFileAdded(string diff)
    {
        var headers = HunkHeader.Matches(diff);
        return headers.Count == 1 && WholeFileAdd.IsMatch(diff);
    }

    // "@@ -0,0 +1,N @@": il lato vecchio e' vuoto. Con il contesto standard una riga aggiunta in cima a un file non vuoto ha "-1,3".
    private static readonly Regex WholeFileAdd = new(@"^@@ -0,0 \+1(?:,\d+)? @@", RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
