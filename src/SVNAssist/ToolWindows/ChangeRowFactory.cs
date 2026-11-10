using System.IO;
using SVNAssist.Models;

namespace SVNAssist.ToolWindows;

/// <summary>
/// Trasforma gli elementi di <c>svn status</c> nell'elenco appiattito di righe (sezioni, cartelle, file)
/// mostrato dalla ListBox. Pura: niente stato, niente dipendenze da VS.
/// </summary>
internal static class ChangeRowFactory
{
    internal const string ExpandedChevron = "▾";
    internal const string CollapsedChevron = "▸";
    private const double BaseIndent = 8;
    private const double IndentStep = 14;

    private static readonly ChangeSection[] SectionOrder = [ChangeSection.Conflicts, ChangeSection.Changes, ChangeSection.Unversioned];

    /// <summary>Chiave della riga di sezione (usata anche in <paramref name="collapsed"/>).</summary>
    internal static string SectionKey(ChangeSection section) => $"sec:{section}";

    /// <summary>Titolo della sezione.</summary>
    internal static string SectionTitle(ChangeSection section) => section switch
    {
        ChangeSection.Conflicts => "Conflitti",
        ChangeSection.Changes => "Modifiche",
        _ => "Non versionati",
    };

    /// <summary>Alberi (o liste) per sezione, nell'ordine di visualizzazione.</summary>
    internal static IEnumerable<(ChangeSection Section, List<ChangeEntry> Entries)> Sections(IEnumerable<ChangeEntry> entries)
    {
        var all = entries.ToList();
        foreach (var section in SectionOrder)
        {
            var inSection = all.Where(e => e.Section == section).ToList();
            if (inSection.Count > 0)
                yield return (section, inSection);
        }
    }

    /// <summary>Costruisce le righe per la vista corrente.</summary>
    /// <param name="entries">Tutti gli elementi.</param>
    /// <param name="treeView"><c>true</c> per l'albero, <c>false</c> per la lista.</param>
    /// <param name="collapsed">Chiavi di sezioni e cartelle compresse.</param>
    /// <param name="workingCopyPath">Root della working copy (per i percorsi completi delle cartelle).</param>
    internal static List<ChangeRowViewModel> Build(
        IEnumerable<ChangeEntry> entries,
        bool treeView,
        ISet<string> collapsed,
        string workingCopyPath)
    {
        var rows = new List<ChangeRowViewModel>();
        foreach (var (section, inSection) in Sections(entries))
        {
            var key = SectionKey(section);
            var isCollapsed = collapsed.Contains(key);
            rows.Add(new ChangeRowViewModel(ChangeRowKind.Section, key, section, inSection)
            {
                Chevron = isCollapsed ? CollapsedChevron : ExpandedChevron,
                Name = $"{SectionTitle(section)} ({inSection.Count})",
                ToolTip = isCollapsed ? "Clic per espandere" : "Clic per comprimere",
                CanAdd = section == ChangeSection.Unversioned,
                CanRevert = section == ChangeSection.Changes,
            });

            if (isCollapsed)
                continue;

            var nodes = treeView
                ? ChangesTreeBuilder.BuildTree(section, inSection)
                : ChangesTreeBuilder.BuildList(section, inSection);
            AddNodes(rows, nodes, section, depth: 0, treeView, collapsed, workingCopyPath);
        }

        return rows;
    }

    private static void AddNodes(
        List<ChangeRowViewModel> rows,
        IReadOnlyList<ChangeNode> nodes,
        ChangeSection section,
        int depth,
        bool treeView,
        ISet<string> collapsed,
        string workingCopyPath)
    {
        foreach (var node in nodes)
        {
            var indent = BaseIndent + depth * IndentStep;

            if (node.IsFolder)
            {
                var expanded = !collapsed.Contains(node.Key);
                rows.Add(new ChangeRowViewModel(ChangeRowKind.Folder, node.Key, section, node.Entries().ToList())
                {
                    FolderFullPath = Path.Combine(workingCopyPath, node.FolderPath),
                    FolderRelativePath = node.FolderPath,
                    IndentWidth = indent,
                    Chevron = expanded ? ExpandedChevron : CollapsedChevron,
                    Name = node.Label,
                    Detail = node.Count.ToString(),
                    ToolTip = node.FolderPath,
                    CanAdd = section == ChangeSection.Unversioned,
                    CanIgnore = section == ChangeSection.Unversioned,
                    CanRevert = section != ChangeSection.Unversioned,
                    CanDiff = section != ChangeSection.Unversioned,
                });

                if (expanded)
                    AddNodes(rows, node.Children, section, depth + 1, treeView, collapsed, workingCopyPath);
                continue;
            }

            var entry = node.Entry!;
            var status = entry.Status;
            var isRoot = entry.RelativePath == ".";
            rows.Add(new ChangeRowViewModel(ChangeRowKind.File, $"{node.Key}|{status}", section, [entry])
            {
                IndentWidth = indent,
                StatusText = StatusPresentation.ShortCode(status),
                StatusColor = StatusPresentation.Color(status),
                Name = isRoot ? "(cartella radice)" : node.Label + (entry.IsDirectory ? "\\" : string.Empty),
                Detail = treeView ? string.Empty : entry.FolderPath,
                ToolTip = $"{entry.FullPath}\n{StatusPresentation.DisplayName(status, entry.IsTreeConflict)}",
                CanAdd = status == SvnStatusKind.Unversioned,
                CanIgnore = status == SvnStatusKind.Unversioned,
                CanDelete = status is not (SvnStatusKind.Unversioned or SvnStatusKind.Unknown),
                CanRevert = IsRevertable(status),
                CanResolve = status == SvnStatusKind.Conflicted,
                CanDiff = status != SvnStatusKind.Unversioned,
            });
        }
    }

    /// <summary>Stati su cui <c>svn revert</c> ha senso.</summary>
    internal static bool IsRevertable(SvnStatusKind status) => status is
        SvnStatusKind.Modified or SvnStatusKind.Added or SvnStatusKind.Deleted
        or SvnStatusKind.Conflicted or SvnStatusKind.Missing or SvnStatusKind.Replaced;
}
