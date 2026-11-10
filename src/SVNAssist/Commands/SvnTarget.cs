using System.IO;
using SVNAssist.Services;

namespace SVNAssist.Commands;

/// <summary>
/// Elemento di Solution Explorer su cui agisce un comando del sottomenu SVN:
/// percorso effettivo, tipo (file/cartella) e root della working copy che lo contiene.
/// </summary>
/// <param name="Path">Percorso su cui operare (per solution/progetto: la cartella che le contiene).</param>
/// <param name="IsDirectory"><c>true</c> se <paramref name="Path"/> è una cartella.</param>
/// <param name="WorkingCopyRoot">Root della working copy, oppure <c>null</c> se il percorso non è versionato.</param>
internal sealed record SvnTarget(string Path, bool IsDirectory, string? WorkingCopyRoot)
{
    /// <summary>Estensioni dei file di solution/progetto: sul loro nodo si agisce sulla cartella che li contiene.</summary>
    private static readonly string[] ContainerExtensions =
        [".sln", ".slnx", ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".proj", ".shproj", ".wapproj"];

    /// <summary>Messaggio mostrato quando il percorso non appartiene a una working copy.</summary>
    public const string NotWorkingCopyMessage = "Non è una working copy SVN";

    /// <summary>Cartella in cui lanciare svn: il percorso stesso se è una cartella, altrimenti la sua cartella padre.</summary>
    public string WorkingDirectory => IsDirectory ? Path : System.IO.Path.GetDirectoryName(Path)!;

    /// <summary>
    /// Risolve il percorso selezionato in Solution Explorer. Per i nodi solution/progetto
    /// (selezionati come file .sln/.csproj...) si usa la cartella che li contiene, come fa TortoiseSVN
    /// sulla cartella del progetto. Restituisce <c>null</c> se il percorso è vuoto o non esiste.
    /// </summary>
    public static SvnTarget? Resolve(string? selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
            return null;

        var path = selectedPath.Trim();
        if (Directory.Exists(path))
            return Create(path, isDirectory: true);

        if (!File.Exists(path))
            return null;

        var extension = System.IO.Path.GetExtension(path);
        if (ContainerExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            var folder = System.IO.Path.GetDirectoryName(path);
            return folder is null ? null : Create(folder, isDirectory: true);
        }

        return Create(path, isDirectory: false);
    }

    /// <summary>Elenca al massimo <paramref name="max"/> righe, poi riassume il resto ("... e altri N").</summary>
    public static string FormatList(IReadOnlyList<string> items, int max = 15)
    {
        if (items.Count <= max)
            return string.Join("\n", items);

        return string.Join("\n", items.Take(max)) + $"\n... e altri {items.Count - max}";
    }

    private static SvnTarget Create(string path, bool isDirectory)
    {
        var start = isDirectory ? path : System.IO.Path.GetDirectoryName(path)!;
        return new SvnTarget(path, isDirectory, SvnService.FindSvnRoot(start));
    }
}
