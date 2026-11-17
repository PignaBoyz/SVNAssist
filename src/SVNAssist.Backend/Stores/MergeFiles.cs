using System.Text.Json.Serialization;
using SVNAssist.Models;

namespace SVNAssist.Stores;

/// <summary>Un file (o cartella) toccato dalle revisioni da unire. <c>N</c> = quante delle revisioni considerate lo toccano (0 = non mostrato); <c>C</c> = l'anteprima prevede un conflitto.</summary>
internal sealed record MergeFileDto(
    string A,
    string P,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int N = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? K = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool C = false);

/// <summary>File coinvolti dalla scelta (o dalla revisione indicata dal menu contestuale, <c>Focus</c>). <c>Files</c> = file veri (cartelle escluse), <c>More</c> = voci non elencate.</summary>
internal sealed record MergeFilesDto(string Subject, bool Focus, bool Loading, string Error, IReadOnlyList<MergeFileDto> Items, int Files, int More);

/// <summary>Anteprima raggruppata per revisione: i file che la revisione tocca (<c>svn log -v</c>), con i conflitti previsti dal dry-run complessivo.</summary>
internal sealed record MergeRevFilesDto(long Rev, string Author, string Msg, IReadOnlyList<MergeFileDto> Files, int More, int Conflicts);

/// <summary>Elenchi di file per revisione del Log in modalita' merge: percorsi relativi al ramo sorgente, unione di piu' revisioni, conflitti previsti.</summary>
internal static class MergeFiles
{
    internal const int MaxItems = 2000;
    internal const int MaxPerRevision = 200;
    internal const int MaxRevisionsInPreview = 200;

    /// <summary>
    /// Percorso del repository (<c>/branches/x/src/a.cs</c>) relativo al ramo sorgente (<c>branches/x</c>) = percorso nella working copy.
    /// <c>null</c> = la cartella del ramo stessa (nulla da mostrare); fuori dal ramo resta il percorso intero, con la barra iniziale.
    /// </summary>
    internal static string? Relative(string repoPath, string sourcePath)
    {
        var p = repoPath.Replace('\\', '/');
        var root = "/" + sourcePath.Trim('/');
        if (root == "/")
            return p.TrimStart('/');
        if (string.Equals(p.TrimEnd('/'), root, StringComparison.Ordinal))
            return null;
        return p.StartsWith(root + "/", StringComparison.Ordinal) ? p[(root.Length + 1)..] : p;
    }

    /// <summary>Elenco di una revisione: file e cartelle del ramo, ordinati; <paramref name="conflicts"/> = percorsi (relativi, con <c>/</c>) che l'anteprima prevede in conflitto.</summary>
    internal static List<MergeFileDto> ForRevision(IReadOnlyList<SvnLogPath> paths, string sourcePath, ISet<string>? conflicts = null)
        => paths.Select(p => (P: Relative(p.Path, sourcePath), p.Action, p.Kind)).Where(x => x.P is not null)
            .Select(x => new MergeFileDto(Normalize(x.Action), x.P!, K: x.Kind == "dir" ? "dir" : null, C: conflicts?.Contains(x.P!) == true))
            .OrderBy(f => f.P, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Unione dei file di piu' revisioni (in ordine di revisione): un file per percorso con l'effetto netto. Aggiunto e poi eliminato = nessun effetto
    /// (non compare); eliminato e poi aggiunto = sostituito; aggiunto e poi modificato = aggiunto.
    /// </summary>
    internal static List<MergeFileDto> Union(IEnumerable<long> revs, Func<long, IReadOnlyList<SvnLogPath>?> pathsOf, string sourcePath)
    {
        var map = new Dictionary<string, (string First, string Last, int Count, string? Kind)>(StringComparer.Ordinal);
        foreach (var r in revs.Distinct().Order())
        {
            foreach (var p in pathsOf(r) ?? [])
            {
                if (Relative(p.Path, sourcePath) is not { } rel)
                    continue;
                var a = Normalize(p.Action);
                map[rel] = map.TryGetValue(rel, out var cur) ? (cur.First, a, cur.Count + 1, cur.Kind ?? NullIfEmpty(p.Kind)) : (a, a, 1, NullIfEmpty(p.Kind));
            }
        }

        var items = new List<MergeFileDto>();
        foreach (var (path, v) in map)
        {
            var net = NetAction(v.First, v.Last);
            if (net is not null)
                items.Add(new MergeFileDto(net, path, v.Count, v.Kind == "dir" ? "dir" : null));
        }

        return items.OrderBy(f => f.P, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Effetto netto di una sequenza di azioni su un percorso; <c>null</c> se si annulla (aggiunto e poi eliminato).</summary>
    internal static string? NetAction(string first, string last)
    {
        if (first == "A" && last == "D")
            return null;
        if (last == "D")
            return "D";
        if (first == "A")
            return "A";
        return first is "D" or "R" || last == "R" ? "R" : "M";
    }

    private static string Normalize(string action) => action is "A" or "M" or "D" or "R" ? action : "M";

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>Taglia l'elenco a <paramref name="max"/> voci: (voci mostrate, quante sono state tolte).</summary>
    internal static (IReadOnlyList<MergeFileDto> Items, int More) Cap(IReadOnlyList<MergeFileDto> items, int max)
        => items.Count <= max ? (items, 0) : (items.Take(max).ToList(), items.Count - max);

    private static string Plain(string path)
    {
        var p = path.Replace('\\', '/');
        return p.StartsWith("./", StringComparison.Ordinal) ? p[2..] : p;
    }

    /// <summary>Percorsi in conflitto di un'anteprima, normalizzati (<c>/</c>) per il confronto con i percorsi del log.</summary>
    internal static HashSet<string> ConflictPaths(MergeOutput o)
        => o.Items.Where(i => i.Conflicted).Select(i => Plain(i.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
