using System.Text;

namespace SVNAssist.Services;

/// <summary>
/// Backup automatico prima di un revert distruttivo: il <c>svn diff</c> dei file che si annullano in
/// <c>&lt;working copy&gt;/.svn-assist/backups/&lt;data-ora&gt;-&lt;operazione&gt;.patch</c>. La cartella <c>.svn-assist</c> non e' mai
/// una modifica per SVNAssist (esclusa dal parser di stato, dal cleanup e dal file watcher): niente proprieta' svn toccate nel repo.
/// Vale solo per il testo: i file binari non stanno in un diff (<see cref="BinaryFiles"/>) e le modifiche a file non UTF-8 possono perdere caratteri.
/// </summary>
internal static class PatchBackup
{
    internal const string FolderName = ".svn-assist";
    internal const long MaxFolderBytes = 200L * 1024 * 1024;
    internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>Spazio libero del disco che contiene il percorso; sostituibile nei test. Percorso non misurabile (rete): "infinito", si fida della scrittura.</summary>
    internal static Func<string, long> FreeSpace = path =>
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch (Exception) { return long.MaxValue; }
    };

    internal static string BackupsFolder(string wc) => Path.Combine(wc, FolderName, "backups");

    /// <summary>Spazio che serve per scrivere <paramref name="textBytes"/> byte: il testo piu' un margine (10%, almeno 1 MB).</summary>
    internal static long Needed(long textBytes) => textBytes + Math.Max(1024 * 1024, textBytes / 10);

    /// <summary>Scrive il backup e ritorna il percorso. Lancia <see cref="IOException"/> se lo spazio non basta (<see cref="NoSpaceException"/>) o la scrittura fallisce.</summary>
    internal static string Write(string wc, string operation, string patchText, DateTime? now = null)
    {
        var bytes = new UTF8Encoding(false).GetBytes(patchText);
        var folder = BackupsFolder(wc);
        var needed = Needed(bytes.Length);
        var free = FreeSpace(wc);
        if (free < needed)
            throw new NoSpaceException(needed, free);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{(now ?? DateTime.Now):yyyyMMdd-HHmmss}-{operation}.patch");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Elimina i backup piu' vecchi di <see cref="MaxAge"/> e, se la cartella supera <see cref="MaxFolderBytes"/>, i piu' vecchi finche' rientra. Mai un'eccezione.</summary>
    internal static void Prune(string wc, DateTime? now = null)
    {
        try
        {
            var dir = new DirectoryInfo(BackupsFolder(wc));
            if (!dir.Exists)
                return;
            var files = dir.GetFiles("*.patch").OrderBy(f => f.LastWriteTimeUtc).ToList();
            var limit = (now ?? DateTime.Now).ToUniversalTime() - MaxAge;
            foreach (var f in files.Where(f => f.LastWriteTimeUtc < limit).ToList())
            {
                f.Delete();
                files.Remove(f);
            }

            var total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= MaxFolderBytes)
                    break;
                total -= f.Length;
                f.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort: la pulizia non deve fermare un'operazione
        }
    }

    internal static void Delete(string? path)
    {
        try { if (path is not null) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    /// <summary>File binari con modifiche (svn li salta nel diff: "Cannot display: file marked as a binary type.").</summary>
    internal static List<string> BinaryFiles(string diff)
    {
        var result = new List<string>();
        string? current = null;
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Index: ", StringComparison.Ordinal))
                current = line[7..];
            else if (current is not null && line.StartsWith("Cannot display: file marked as a binary type", StringComparison.Ordinal))
            {
                result.Add(current);
                current = null;
            }
        }

        return result;
    }

    internal static string FormatBytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    internal sealed class NoSpaceException(long needed, long free) : IOException("no space")
    {
        public long Needed { get; } = needed;
        public long Free { get; } = free;
    }
}
