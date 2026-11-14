using System.Xml.Linq;

namespace SVNAssist.Services;

/// <summary>Esito dell'eliminazione di non versionati/ignorati: eliminati e saltati perche' in uso.</summary>
public sealed record CleanupReport(int Deleted, int Skipped)
{
    public string Summary(string cleanupDone) => Skipped > 0
        ? cleanupDone + " " + Loc.T("Eliminati {0}, saltati {1} perché in uso (chiudi la soluzione o ferma il debug e riprova).", Deleted, Skipped)
        : Deleted > 0 ? cleanupDone + " " + Loc.T("Eliminati {0}.", Deleted) : cleanupDone;
}

/// <summary>
/// Elimina da se' i non versionati/ignorati (al posto di <c>--remove-unversioned/--remove-ignored</c>, che si ferma al primo file tenuto
/// aperto da Visual Studio). Definitiva come svn; <c>.vs</c> (stato interno di VS) si salta sempre; i file in uso si saltano senza fermarsi.
/// </summary>
internal static class CleanupDeleter
{
    /// <summary>Percorsi assoluti con stato <c>unversioned</c>/<c>ignored</c> nell'XML di <c>svn status --no-ignore --xml</c>, senza quelli dentro una <c>.vs</c>.</summary>
    internal static List<string> ParseExtras(string xml, string root, bool unversioned, bool ignored)
    {
        var result = new List<string>();
        foreach (var e in XDocument.Parse(xml).Descendants("entry"))
        {
            var item = e.Element("wc-status")?.Attribute("item")?.Value;
            var path = e.Attribute("path")?.Value;
            if (string.IsNullOrEmpty(path) || !(item == "unversioned" && unversioned || item == "ignored" && ignored))
                continue;
            var full = Path.GetFullPath(Path.Combine(root, path));
            if (!full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".vs", StringComparer.OrdinalIgnoreCase))
                result.Add(full);
        }

        return result;
    }

    internal static CleanupReport Delete(IEnumerable<string> paths, Action<string>? sink)
    {
        int deleted = 0, skipped = 0;
        foreach (var p in paths)
        {
            if (Directory.Exists(p) ? DeleteDir(p, sink, ref skipped) : DeleteFile(p, sink, ref skipped))
            {
                deleted++;
                sink?.Invoke("D         " + p);
            }
        }

        return new CleanupReport(deleted, skipped);
    }

    private static bool DeleteFile(string path, Action<string>? sink, ref int skipped)
    {
        try
        {
            if (!File.Exists(path))
                return true; // gia' sparito
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped++;
            sink?.Invoke(Loc.T("Saltato (in uso): {0}", path));
            return false;
        }
    }

    private static bool DeleteDir(string dir, Action<string>? sink, ref int skipped)
    {
        var all = true;
        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0) // un link si toglie senza entrare
        {
            foreach (var e in Directory.EnumerateFileSystemEntries(dir).ToList())
            {
                if (Path.GetFileName(e).Equals(".vs", StringComparison.OrdinalIgnoreCase))
                    all = false;
                else if (!(Directory.Exists(e) ? DeleteDir(e, sink, ref skipped) : DeleteFile(e, sink, ref skipped)))
                    all = false;
            }
        }

        if (!all)
            return false;
        try { Directory.Delete(dir); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped++;
            sink?.Invoke(Loc.T("Saltato (in uso): {0}", dir));
            return false;
        }
    }
}
