using SVNAssist.Models;

namespace SVNAssist.Stores;

/// <summary>Sezione del pannello Changes in cui compare un elemento.</summary>
internal enum ChangeSection
{
    /// <summary>Elementi scelti per il commit (insieme tenuto dall'estensione: SVN non ha uno staging).</summary>
    Staged,

    /// <summary>Modifiche a elementi versionati (M/A/D/R/!) e conflitti (C).</summary>
    Changes,

    /// <summary>Elementi non versionati (?).</summary>
    Unversioned,
}

/// <summary>Un elemento riportato da <c>svn status</c>, pronto per essere raggruppato.</summary>
/// <param name="FullPath">Percorso completo (da <c>svn status</c>, mai dalla UI).</param>
/// <param name="RelativePath">Percorso relativo alla working copy, con <c>/</c> come separatore ("." = la radice).</param>
/// <param name="Status">Stato SVN.</param>
/// <param name="IsTreeConflict">Conflitto "ad albero".</param>
/// <param name="IsDirectory">L'elemento e' una cartella (es. cartella non versionata o con sole proprieta' modificate).</param>
internal sealed record ChangeEntry(
    string FullPath,
    string RelativePath,
    SvnStatusKind Status,
    bool IsTreeConflict = false,
    bool IsDirectory = false)
{
    /// <summary>Nome dell'elemento (ultimo segmento del percorso).</summary>
    public string Name => ChangesTreeBuilder.SplitPath(RelativePath) is { Length: > 0 } s ? s[^1] : RelativePath;

    /// <summary>Cartella che lo contiene, relativa alla working copy ("" se e' nella radice), con <c>/</c>.</summary>
    public string FolderPath
    {
        get
        {
            var segments = ChangesTreeBuilder.SplitPath(RelativePath);
            return segments.Length <= 1 ? string.Empty : string.Join('/', segments[..^1]);
        }
    }
}

/// <summary>Nodo dell'albero: una cartella (eventualmente compattata, es. "Core/Services") o un file.</summary>
internal sealed class ChangeNode
{
    private ChangeNode(string key, string label, string folderPath, ChangeEntry? entry)
    {
        Key = key;
        Label = label;
        FolderPath = folderPath;
        Entry = entry;
    }

    /// <summary>Chiave di riga: <c>"&lt;sezione&gt;:&lt;percorso/con/slash&gt;"</c> (protocollo UI).</summary>
    public string Key { get; private set; }

    /// <summary>Testo mostrato; per le cartelle compattate contiene piu' segmenti ("Core/Services").</summary>
    public string Label { get; private set; }

    /// <summary>Percorso relativo della cartella (la piu' profonda, se compattata). Vuoto per i file.</summary>
    public string FolderPath { get; private set; }

    /// <summary>L'elemento, se il nodo e' un file; <c>null</c> per le cartelle.</summary>
    public ChangeEntry? Entry { get; }

    /// <summary>
    /// Elemento-cartella che coincide con questa cartella dell'albero (es. cartella aggiunta/modificata che ha anche figli
    /// elencati): non compare come riga a se' ma e' contato e incluso nelle azioni sulla cartella.
    /// </summary>
    public ChangeEntry? Self { get; private set; }

    /// <summary><c>true</c> per le cartelle.</summary>
    public bool IsFolder => Entry is null;

    /// <summary>Figli ordinati (cartelle prima, poi file).</summary>
    public List<ChangeNode> Children { get; } = [];

    /// <summary>Numero di elementi modificati contenuti (1 per un file).</summary>
    public int Count { get; private set; }

    /// <summary>Tutti gli elementi contenuti nel nodo (se stesso se e' un file).</summary>
    public IEnumerable<ChangeEntry> Entries()
    {
        if (Entry is not null)
        {
            yield return Entry;
            yield break;
        }

        if (Self is not null)
            yield return Self;

        foreach (var child in Children)
            foreach (var entry in child.Entries())
                yield return entry;
    }

    internal static ChangeNode Root() => new(string.Empty, string.Empty, string.Empty, null);

    internal static ChangeNode Leaf(ChangeSection section, ChangeEntry entry, string label)
        => new($"{ChangesTreeBuilder.SectionId(section)}:{entry.RelativePath}", label, string.Empty, entry) { Count = 1 };

    internal ChangeNode GetOrAddFolder(string name)
    {
        var existing = Children.FirstOrDefault(c => c.IsFolder && string.Equals(c.Label, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        var path = FolderPath.Length == 0 ? name : FolderPath + "/" + name;
        var folder = new ChangeNode(string.Empty, name, path, null);
        Children.Add(folder);
        return folder;
    }

    internal void SetSelf(ChangeEntry entry) => Self = entry;

    /// <summary>Compatta le catene di cartelle con un solo figlio-cartella, ordina e calcola chiavi e conteggi.</summary>
    internal void Finish(ChangeSection section)
    {
        foreach (var child in Children.Where(c => c.IsFolder))
        {
            // Come Git: A -> B -> file diventa "A/B" -> file; si compatta solo con un unico figlio che e' una cartella
            // e se la cartella intermedia non ha un elemento proprio (altrimenti sparirebbe dalle azioni).
            var current = child;
            while (current.Children.Count == 1 && current.Children[0].IsFolder && current.Self is null)
            {
                var only = current.Children[0];
                current.Label = current.Label + "/" + only.Label;
                current.FolderPath = only.FolderPath;
                current.Self = only.Self;
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
            Key = $"{ChangesTreeBuilder.SectionId(section)}:{FolderPath}";
            Count = Children.Sum(c => c.Count) + (Self is null ? 0 : 1);
        }
    }
}

/// <summary>
/// Raggruppa gli elementi di <c>svn status</c> in alberi di cartelle e liste.
/// Logica pura, senza dipendenze dall'host.
/// </summary>
internal static class ChangesTreeBuilder
{
    /// <summary>Id di sezione usato nelle chiavi di riga e nello snapshot.</summary>
    internal static string SectionId(ChangeSection section) => section switch
    {
        ChangeSection.Staged => "staged",
        ChangeSection.Changes => "changes",
        _ => "unversioned",
    };

    /// <summary>Titolo della sezione.</summary>
    internal static string SectionTitle(ChangeSection section) => section switch
    {
        ChangeSection.Staged => "Staged",
        ChangeSection.Changes => "Modifiche",
        _ => "Non versionati",
    };

    /// <summary>Divide un percorso relativo nei suoi segmenti (accetta <c>\</c> e <c>/</c>).</summary>
    internal static string[] SplitPath(string relativePath)
        => relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Costruisce l'albero (cartelle compattate) per gli elementi di una sezione.</summary>
    public static IReadOnlyList<ChangeNode> BuildTree(ChangeSection section, IEnumerable<ChangeEntry> entries)
    {
        var all = entries.ToList();
        // Cartelle che hanno elementi sotto: un elemento-cartella coincidente diventa la cartella stessa, non una riga doppia.
        var parentsWithChildren = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in all)
        {
            var segments = SplitPath(entry.RelativePath);
            for (var i = 1; i < segments.Length; i++)
                parentsWithChildren.Add(string.Join('/', segments[..i]));
        }

        var root = ChangeNode.Root();
        foreach (var entry in all)
        {
            var segments = SplitPath(entry.RelativePath);
            if (segments.Length == 0)
                segments = [entry.RelativePath];

            var parent = root;
            for (var i = 0; i < segments.Length - 1; i++)
                parent = parent.GetOrAddFolder(segments[i]);

            if (entry.IsDirectory && parentsWithChildren.Contains(string.Join('/', segments)))
                parent.GetOrAddFolder(segments[^1]).SetSelf(entry);
            else
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
}

/// <summary>Come mostrare gli stati SVN: lettera (enum dello schema), descrizione.</summary>
internal static class StatusPresentation
{
    /// <summary>Lettera dello schema UI: M A D C ? ! R. Gli stati anomali (ostruito, incompleto, mancante) sono tutti "!".</summary>
    public static string ShortCode(SvnStatusKind status) => status switch
    {
        SvnStatusKind.Modified => "M",
        SvnStatusKind.Added => "A",
        SvnStatusKind.Deleted => "D",
        SvnStatusKind.Conflicted => "C",
        SvnStatusKind.Unversioned => "?",
        SvnStatusKind.Replaced => "R",
        SvnStatusKind.Missing or SvnStatusKind.Obstructed or SvnStatusKind.Incomplete => "!",
        _ => "?",
    };

    /// <summary>Descrizione in italiano dello stato.</summary>
    public static string DisplayName(SvnStatusKind status, bool isTreeConflict) => status switch
    {
        SvnStatusKind.Conflicted => isTreeConflict ? "Conflitto (albero)" : "Conflitto",
        SvnStatusKind.Modified => "Modificato",
        SvnStatusKind.Added => "Aggiunto",
        SvnStatusKind.Deleted => "Eliminato",
        SvnStatusKind.Unversioned => "Non versionato",
        SvnStatusKind.Missing => "Mancante",
        SvnStatusKind.Replaced => "Sostituito",
        SvnStatusKind.Obstructed => "Ostruito",
        SvnStatusKind.Incomplete => "Incompleto",
        SvnStatusKind.Normal => "Normale",
        _ => "Sconosciuto",
    };

    /// <summary>Stati su cui <c>svn revert</c> ha senso.</summary>
    internal static bool IsRevertable(SvnStatusKind status) => status is
        SvnStatusKind.Modified or SvnStatusKind.Added or SvnStatusKind.Deleted
        or SvnStatusKind.Conflicted or SvnStatusKind.Missing or SvnStatusKind.Replaced;
}
