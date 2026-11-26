using System.Globalization;
using System.Text;

namespace SVNAssist.Services;

/// <summary>Ruolo di un ramo: collegato a un work item, ramo di rilascio (principale) o nessuno.</summary>
internal enum BranchRole
{
    None,
    WorkItem,
    Release,
}

/// <param name="Path">Percorso del ramo nel repository (<c>/branches/core19_x</c>, <c>/trunk</c>).</param>
internal sealed record BranchLink(string Path, BranchRole Role, int WorkItem = 0);

/// <summary>
/// File nascosto <c>.svnassist</c> nella radice del ramo, versionato: una riga per ramo, <c>percorso = valore</c>
/// (<c>81018</c> = work item, <c>rilascio</c> = ramo di rilascio, <c>nessuno</c>).
/// Una riga per ramo perche' il file viaggia con copie e merge: il rientro di un ramo aggiunge la sua riga invece di sostituire
/// quella del ramo di destinazione, e ogni ramo legge solo la riga del proprio percorso (le altre si ignorano).
/// Non serve un commit apposta: il file cambiato entra nel primo commit dell'utente.
/// </summary>
internal static class BranchLinks
{
    internal const string FileName = ".svnassist";

    private const string Header = "# SVNAssist: ramo -> work item di Azure DevOps (numero), \"rilascio\" = ramo principale, \"nessuno\". Una riga per ramo.";

    /// <summary>Percorso del ramo nel repository da URL del ramo e radice del repository (decodificato, con <c>/</c> iniziale, senza <c>/</c> finale).</summary>
    internal static string PathOf(string branchUrl, string repoRoot)
    {
        var url = (branchUrl ?? string.Empty).TrimEnd('/');
        var root = (repoRoot ?? string.Empty).TrimEnd('/');
        if (root.Length == 0 || !url.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        var rel = Uri.UnescapeDataString(url[root.Length..]);
        return rel.Length == 0 ? "/" : "/" + rel.TrimStart('/');
    }

    /// <summary>Tutte le righe valide del file (le altre, commenti compresi, si ignorano).</summary>
    internal static IReadOnlyList<BranchLink> Parse(string? text)
    {
        var list = new List<BranchLink>();
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var eq = line.LastIndexOf('=');
            if (eq <= 0)
                continue;
            var path = Norm(line[..eq]);
            var value = line[(eq + 1)..].Trim().ToLowerInvariant();
            if (path.Length == 0)
                continue;
            if (int.TryParse(value.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                list.Add(new BranchLink(path, BranchRole.WorkItem, id));
            else if (value is "rilascio" or "release")
                list.Add(new BranchLink(path, BranchRole.Release));
            else if (value is "nessuno" or "none")
                list.Add(new BranchLink(path, BranchRole.None));
        }

        return list;
    }

    /// <summary>Il collegamento del ramo <paramref name="branchPath"/>: l'ultima riga con quel percorso; nessuno se manca.</summary>
    internal static BranchLink For(string? text, string branchPath)
    {
        var p = Norm(branchPath);
        return Parse(text).LastOrDefault(l => string.Equals(l.Path, p, StringComparison.Ordinal)) ?? new BranchLink(p, BranchRole.None);
    }

    /// <summary>Testo del file con la riga del ramo aggiornata (le righe degli altri rami restano com'erano).</summary>
    internal static string With(string? text, BranchLink link)
    {
        var p = Norm(link.Path);
        var value = link.Role switch
        {
            BranchRole.WorkItem => link.WorkItem.ToString(CultureInfo.InvariantCulture),
            BranchRole.Release => "rilascio",
            _ => "nessuno",
        };
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0)
            lines.Add(Header);
        var kept = lines.Where(l =>
        {
            var eq = l.LastIndexOf('=');
            return l.TrimStart().StartsWith('#') || eq <= 0 || !string.Equals(Norm(l[..eq]), p, StringComparison.Ordinal);
        }).ToList();
        kept.Add($"{p} = {value}");
        var sb = new StringBuilder();
        foreach (var l in kept)
            sb.Append(l).Append("\r\n");
        return sb.ToString();
    }

    private static string Norm(string path)
    {
        var p = path.Trim().Replace('\\', '/').TrimEnd('/');
        return p.Length == 0 ? string.Empty : p.StartsWith('/') ? p : "/" + p;
    }

    /// <summary>Legge il file della working copy (vuoto se non c'e').</summary>
    internal static string Read(string workingCopyRoot)
    {
        var f = System.IO.Path.Combine(workingCopyRoot, FileName);
        try { return File.Exists(f) ? File.ReadAllText(f) : string.Empty; }
        catch (IOException) { return string.Empty; }
        catch (UnauthorizedAccessException) { return string.Empty; }
    }

    /// <summary>Scrive il file nella working copy e lo nasconde (Windows). true se il file e' nuovo (va aggiunto a svn).</summary>
    internal static bool Write(string workingCopyRoot, string text)
    {
        var f = System.IO.Path.Combine(workingCopyRoot, FileName);
        var isNew = !File.Exists(f);
        if (!isNew)
            File.SetAttributes(f, File.GetAttributes(f) & ~FileAttributes.Hidden & ~FileAttributes.ReadOnly);
        File.WriteAllText(f, text, new UTF8Encoding(false));
        if (OperatingSystem.IsWindows())
            File.SetAttributes(f, File.GetAttributes(f) | FileAttributes.Hidden);
        return isNew;
    }
}
