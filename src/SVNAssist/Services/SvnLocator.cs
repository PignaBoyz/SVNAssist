using System.IO;

namespace SVNAssist.Services;

/// <summary>
/// Localizza gli eseguibili di Subversion (<c>svn.exe</c>, <c>svnadmin.exe</c>)
/// e del client nativo TortoiseSVN (<c>TortoiseProc.exe</c>).
/// </summary>
/// <remarks>
/// Logica di ricerca centralizzata in un unico punto (prima era duplicata tra
/// <see cref="SvnService"/> e i test). Cerca prima nel PATH di sistema, poi nelle
/// posizioni di installazione note dei principali client SVN per Windows.
///
/// È usata sia da <see cref="SvnService"/> per trovare l'eseguibile, sia dalla
/// verifica all'avvio dell'estensione che propone l'installazione di SVN se mancante.
/// </remarks>
public static class SvnLocator
{
    /// <summary>Restituisce il percorso di <c>svn.exe</c>, o <c>null</c> se non trovato.</summary>
    public static string? FindSvn() => FindExecutable("svn.exe");

    /// <summary>Restituisce il percorso di <c>svnadmin.exe</c>, o <c>null</c> se non trovato.</summary>
    public static string? FindSvnAdmin() => FindExecutable("svnadmin.exe");

    /// <summary>Restituisce il percorso di <c>TortoiseProc.exe</c>, o <c>null</c> se non trovato.</summary>
    public static string? FindTortoiseProc() => FindExecutable("TortoiseProc.exe");

    /// <summary><c>true</c> se <c>svn.exe</c> è disponibile sulla macchina.</summary>
    public static bool IsSvnAvailable => FindSvn() is not null;

    /// <summary>
    /// Cerca un eseguibile nel PATH di sistema e nelle posizioni di installazione note.
    /// </summary>
    /// <param name="exeName">Nome del file eseguibile (es. <c>svn.exe</c>).</param>
    /// <returns>Il percorso completo, oppure <c>null</c> se non trovato.</returns>
    public static string? FindExecutable(string exeName)
    {
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate))
                return candidate;
        }

        foreach (var dir in KnownBinDirectories())
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Cartelle <c>bin</c> note dei client SVN per Windows
    /// (TortoiseSVN, SlikSVN, CollabNet/Subversion).
    /// </summary>
    private static IEnumerable<string> KnownBinDirectories()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        yield return Path.Combine(programFiles, "TortoiseSVN", "bin");
        yield return Path.Combine(programFilesX86, "TortoiseSVN", "bin");
        yield return Path.Combine(programFiles, "SlikSvn", "bin");
        yield return Path.Combine(programFiles, "Subversion", "bin");
        yield return Path.Combine(programFiles, "CollabNet", "Subversion Client");
    }
}
