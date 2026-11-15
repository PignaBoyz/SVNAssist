using System.Globalization;
using System.Text;

namespace SVNAssist.Services;

/// <summary>Uno shelve: <c>Id</c> = nome del file senza estensione.</summary>
internal sealed record ShelveInfo(string Id, string Name, DateTime Date, int Files);

/// <summary>
/// Shelve = una patch normale (<c>svn patch</c> / TortoiseSVN) in <c>&lt;working copy&gt;/.svn-assist/shelves/&lt;data-ora&gt;-&lt;nome&gt;.patch</c>.
/// I metadati sono commenti <c>#</c> in testa, che <c>svn patch</c> ignora. Come i backup (<see cref="PatchBackup"/>) la cartella non e' mai una modifica
/// per SVNAssist; il prune dei backup guarda solo <c>backups/</c>, quindi non tocca mai gli shelve.
/// </summary>
internal static class ShelveFiles
{
    internal static string Folder(string wc) => Path.Combine(wc, PatchBackup.FolderName, "shelves");

    /// <summary>File dello shelve <paramref name="id"/>; un id con separatori di percorso non e' valido (viene dalla UI).</summary>
    internal static string PathOf(string wc, string id)
        => id.Length > 0 && Path.GetFileName(id) == id ? Path.Combine(Folder(wc), id + ".patch") : throw new ArgumentException("id");

    private static string OneLine(string s) => string.Join(' ', s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    internal static string Sanitize(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(OneLine(name).Select(c => bad.Contains(c) || char.IsWhiteSpace(c) ? '-' : c).ToArray()).Trim('-', '.');
        return clean.Length == 0 ? "shelve" : clean[..Math.Min(clean.Length, 40)];
    }

    /// <summary>Scrive lo shelve e ritorna il percorso. <see cref="PatchBackup.NoSpaceException"/> se lo spazio non basta.</summary>
    internal static string Write(string wc, string name, string diff, int files, string note, DateTime? now = null)
    {
        var when = now ?? DateTime.Now;
        var text = $"# SVNAssist shelve\n# name: {OneLine(name)}\n# date: {when:yyyy-MM-ddTHH:mm:ss}\n# files: {files}\n" +
                   (note.Length > 0 ? $"# note: {OneLine(note)}\n" : string.Empty) + "\n" + diff;
        var bytes = new UTF8Encoding(false).GetBytes(text);
        var needed = PatchBackup.Needed(bytes.Length);
        var free = PatchBackup.FreeSpace(wc);
        if (free < needed)
            throw new PatchBackup.NoSpaceException(needed, free);
        Directory.CreateDirectory(Folder(wc));
        var path = Path.Combine(Folder(wc), $"{when:yyyyMMdd-HHmmss}-{Sanitize(name)}.patch");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Shelve della working copy, il piu' recente per primo. Un file illeggibile o senza intestazione si mostra col nome del file.</summary>
    internal static List<ShelveInfo> List(string wc)
    {
        var result = new List<ShelveInfo>();
        try
        {
            if (!Directory.Exists(Folder(wc)))
                return result;
            foreach (var f in Directory.GetFiles(Folder(wc), "*.patch"))
            {
                string? name = null;
                var date = File.GetLastWriteTime(f);
                var files = 0;
                foreach (var line in File.ReadLines(f).TakeWhile(l => l.StartsWith('#')))
                {
                    if (line.StartsWith("# name: ", StringComparison.Ordinal)) name = line[8..];
                    else if (line.StartsWith("# date: ", StringComparison.Ordinal) && DateTime.TryParse(line[8..], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) date = d;
                    else if (line.StartsWith("# files: ", StringComparison.Ordinal)) int.TryParse(line[9..], out files);
                }

                result.Add(new ShelveInfo(Path.GetFileNameWithoutExtension(f), name ?? Path.GetFileNameWithoutExtension(f), date, files));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort: l'elenco non deve fermare un refresh
        }

        return result.OrderByDescending(s => s.Date).ToList();
    }
}
