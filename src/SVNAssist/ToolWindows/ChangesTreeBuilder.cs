using SVNAssist.Models;

namespace SVNAssist.ToolWindows;

/// <summary>Sezione del pannello in cui compare un elemento.</summary>
internal enum ChangeSection
{
    /// <summary>Conflitti da risolvere.</summary>
    Conflicts,

    /// <summary>Modifiche a elementi versionati (M/A/D/R/!).</summary>
    Changes,

    /// <summary>Elementi non versionati (?).</summary>
    Unversioned,
}

/// <summary>Un elemento riportato da <c>svn status</c>, pronto per essere raggruppato.</summary>
/// <param name="FullPath">Percorso completo.</param>
/// <param name="RelativePath">Percorso relativo alla working copy (separatore <c>\</c> o <c>/</c>).</param>
/// <param name="Status">Stato SVN.</param>
/// <param name="IsTreeConflict">Conflitto "ad albero".</param>
/// <param name="IsDirectory">L'elemento è una cartella (es. cartella non versionata).</param>
internal sealed record ChangeEntry(
    string FullPath,
    string RelativePath,
    SvnStatusKind Status,
    bool IsTreeConflict = false,
    bool IsDirectory = false)
{
    /// <summary>Sezione di appartenenza in base allo stato.</summary>
    public ChangeSection Section => Status switch
    {
        SvnStatusKind.Conflicted => ChangeSection.Conflicts,
        SvnStatusKind.Unversioned => ChangeSection.Unversioned,
        _ => ChangeSection.Changes,
    };

    /// <summary>Nome dell'elemento (ultimo segmento del percorso).</summary>
    public string Name => ChangesTreeBuilder.SplitPath(RelativePath) is { Length: > 0 } s ? s[^1] : RelativePath;

    /// <summary>Cartella che lo contiene, relativa alla working copy ("" se è nella radice).</summary>
    public string FolderPath
    {
        get
        {
            var segments = ChangesTreeBuilder.SplitPath(RelativePath);
            return segments.Length <= 1 ? string.Empty : string.Join('\\', segments[..^1]);
        }
    }
}

/// <summary>Nodo dell'albero: una cartella (eventualmente compattata, es. "Core\Services") o un file.</summary>
internal sealed class ChangeNode
{
    private ChangeNode(string key, string label, string folderPath, ChangeEntry? entry)
    {
        Key = key;
        Label = label;
        FolderPath = folderPath;
        Entry = entry;
    }

    /// <summary>Chiave stabile (sezione + percorso) per ricordare lo stato espanso/compresso.</summary>
    public string Key { get; private set; }

    /// <summary>Testo mostrato; per le cartelle compattate contiene più segmenti ("Core\Services").</summary>
    public string Label { get; private set; }

    /// <summary>Percorso relativo della cartella (la più profonda, se compattata). Vuoto per i file.</summary>
    public string FolderPath { get; private set; }

    /// <summary>L'elemento, se il nodo è un file; <c>null</c> per le cartelle.</summary>
    public ChangeEntry? Entry { get; }

    /// <summary><c>true</c> per le cartelle.</summary>
    public bool IsFolder => Entry is null;

    /// <summary>Figli ordinati (cartelle prima, poi file).</summary>
    public List<ChangeNode> Children { get; } = [];

    /// <summary>Numero di elementi modificati contenuti (1 per un file).</summary>
    public int Count { get; private set; }

    /// <summary>Tutti gli elementi foglia contenuti nel nodo (se stesso se è un file).</summary>
    public IEnumerable<ChangeEntry> Entries()
    {
        if (Entry is not null)
        {
            yield return Entry;
            yield break;
        }

        foreach (var child in Children)
            foreach (var entry in child.Entries())
                yield return entry;
    }

    internal static ChangeNode Root() => new(string.Empty, string.Empty, string.Empty, null);

    internal static ChangeNode Leaf(ChangeSection section, ChangeEntry entry, string label)
        => new($"{section}|{entry.FullPath}", label, string.Empty, entry) { Count = 1 };

    internal ChangeNode GetOrAddFolder(string name)
    {
        var existing = Children.FirstOrDefault(c => c.IsFolder && string.Equals(c.Label, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        var path = FolderPath.Length == 0 ? name : FolderPath + "\\" + name;
        var folder = new ChangeNode(string.Empty, name, path, null);
        Children.Add(folder);
        return folder;
    }

    /// <summary>Compatta le catene di cartelle con un solo figlio-cartella, ordina e calcola chiavi e conteggi.</summary>
    internal void Finish(ChangeSection section)
    {
        foreach (var child in Children.Where(c => c.IsFolder))
        {
            // Come Git: A -> B -> file diventa "A\B" -> file; si compatta solo con un unico figlio che è una cartella.
            var current = child;
            while (current.Children.Count == 1 && current.Children[0].IsFolder)
            {
                var only = current.Children[0];
                current.Label = current.Label + "\\" + only.Label;
                current.FolderPath = only.FolderPath;
                current.Children.Clear();
                current.Children.AddRange(only.Children);
            }

            current.Finish(section);
        }

        Children.Sort((a, b) => a.IsFolder != b.IsFolder
            ? (a.IsFolder ? -1 : 1)
            : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));

        if (IsFolder)
        {
            Key = $"{section}|{FolderPath}";
            Count = Children.Sum(c => c.Count);
        }
    }
}

/// <summary>
/// Raggruppa gli elementi di <c>svn status</c> in sezioni, alberi di cartelle e liste,
/// e calcola lo stato tri-state delle checkbox. Logica pura, senza dipendenze da VS.
/// </summary>
internal static class ChangesTreeBuilder
{
    /// <summary>Divide un percorso relativo nei suoi segmenti (accetta <c>\</c> e <c>/</c>).</summary>
    internal static string[] SplitPath(string relativePath)
        => relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Costruisce l'albero (cartelle compattate) per gli elementi di una sezione.</summary>
    public static IReadOnlyList<ChangeNode> BuildTree(ChangeSection section, IEnumerable<ChangeEntry> entries)
    {
        var root = ChangeNode.Root();
        foreach (var entry in entries)
        {
            var segments = SplitPath(entry.RelativePath);
            if (segments.Length == 0)
                segments = [entry.RelativePath];

            var parent = root;
            for (var i = 0; i < segments.Length - 1; i++)
                parent = parent.GetOrAddFolder(segments[i]);

            parent.Children.Add(ChangeNode.Leaf(section, entry, segments[^1]));
        }

        root.Finish(section);
        return root.Children;
    }

    /// <summary>Elenco piatto ordinato per nome (vista "Lista").</summary>
    public static IReadOnlyList<ChangeNode> BuildList(ChangeSection section, IEnumerable<ChangeEntry> entries)
        => entries
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.FolderPath, StringComparer.OrdinalIgnoreCase)
            .Select(e => ChangeNode.Leaf(section, e, e.Name))
            .ToList();

    /// <summary>Chiavi di tutte le cartelle dell'albero (per "Comprimi tutto").</summary>
    public static IEnumerable<string> FolderKeys(IEnumerable<ChangeNode> nodes)
    {
        foreach (var node in nodes.Where(n => n.IsFolder))
        {
            yield return node.Key;
            foreach (var key in FolderKeys(node.Children))
                yield return key;
        }
    }

    /// <summary>
    /// Stato di una checkbox di gruppo: <c>true</c> se tutti selezionati, <c>false</c> se nessuno,
    /// <c>null</c> (parziale) altrimenti.
    /// </summary>
    public static bool? GetCheckState(IEnumerable<ChangeEntry> entries, Func<ChangeEntry, bool> isSelected)
    {
        var any = false;
        var all = true;
        foreach (var entry in entries)
        {
            if (isSelected(entry))
                any = true;
            else
                all = false;

            if (any && !all)
                return null;
        }

        return any && all;
    }
}
