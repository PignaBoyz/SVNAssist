using System.Diagnostics;
using System.IO;

namespace SVNAssist.Services;

/// <summary>I quattro file di un conflitto per l'editor a 3 vie: base, mia, loro e il file da risolvere (con i marcatori).</summary>
internal sealed record MergeEditorFiles(string Base, string Mine, string Theirs, string Merged);

/// <summary>
/// Editor di merge a 3 vie per un conflitto di testo: prima <c>vsDiffMerge.exe</c> di Visual Studio (<c>theirs mine base merged /m</c>),
/// altrimenti <c>TortoiseMerge.exe</c>; se non c'e' nessuno dei due, il chiamante apre il file con i marcatori nell'editor di VS.
/// L'editor parte e basta (non si aspetta la chiusura): il risultato si salva sul file, poi "Segna come risolto".
/// </summary>
internal static class MergeEditor
{
    internal const string VsDiffMergeName = "vsDiffMerge.exe";
    private static readonly string[] TeamExplorer = ["Common7", "IDE", "CommonExtensions", "Microsoft", "TeamFoundation", "Team Explorer"];

    /// <summary>
    /// <c>vsDiffMerge.exe</c>: prima nel VS che ci ospita (<c>VSAPPIDDIR</c>, ereditata dal backend), poi in ogni installazione sotto
    /// <c>Program Files\Microsoft Visual Studio\&lt;versione&gt;\&lt;edizione&gt;</c> (la piu' recente per prima).
    /// </summary>
    internal static string? FindVsDiffMerge(string? appIdDir = null, IEnumerable<string>? programFilesRoots = null)
    {
        appIdDir ??= Environment.GetEnvironmentVariable("VSAPPIDDIR");
        if (!string.IsNullOrWhiteSpace(appIdDir) && Path.IsPathFullyQualified(appIdDir))
        {
            var own = Path.Combine(appIdDir, "CommonExtensions", "Microsoft", "TeamFoundation", "Team Explorer", VsDiffMergeName);
            if (File.Exists(own))
                return own;
        }

        programFilesRoots ??= [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)];
        foreach (var pf in programFilesRoots.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var vs = Path.Combine(pf, "Microsoft Visual Studio");
            if (!Directory.Exists(vs))
                continue;
            foreach (var version in Directory.GetDirectories(vs).OrderByDescending(Newness))
            foreach (var edition in Directory.GetDirectories(version))
            {
                var exe = Path.Combine([edition, .. TeamExplorer, VsDiffMergeName]);
                if (File.Exists(exe))
                    return exe;
            }
        }

        return null;
    }

    // Cartelle di VS: "2019", "2022" (anni) e dal 2026 il numero di versione ("18"): un numero piccolo e' piu' recente di qualunque anno.
    private static int Newness(string dir) => int.TryParse(Path.GetFileName(dir), out var n) ? (n < 100 ? n + 10000 : n) : 0;

    internal static string? FindTortoiseMerge() => SvnLocator.FindExecutable("TortoiseMerge.exe");

    /// <summary>Argomenti di vsDiffMerge: <c>theirs mine base merged /m</c> (ArgumentList: il quoting lo fa .NET).</summary>
    internal static IReadOnlyList<string> VsArguments(MergeEditorFiles f) => [f.Theirs, f.Mine, f.Base, f.Merged, "/m"];

    /// <summary>Argomenti di TortoiseMerge, come li scrive TortoiseSVN (un solo testo: il suo parser vuole <c>/base:"percorso"</c>).</summary>
    internal static string TortoiseArguments(MergeEditorFiles f)
        => $"/base:\"{f.Base}\" /theirs:\"{f.Theirs}\" /mine:\"{f.Mine}\" /merged:\"{f.Merged}\" " +
           $"/basename:\"{Path.GetFileName(f.Base)}\" /theirsname:\"{Path.GetFileName(f.Theirs)}\" /minename:\"{Path.GetFileName(f.Mine)}\" /mergedname:\"{Path.GetFileName(f.Merged)}\"";

    /// <summary>Avvia l'editor trovato; ritorna il suo nome (<c>vsDiffMerge</c> | <c>TortoiseMerge</c>) o <c>null</c> se nessuno e' disponibile o non parte.</summary>
    internal static string? TryLaunch(MergeEditorFiles f)
    {
        if (FindVsDiffMerge() is { } vs && Start(new ProcessStartInfo(vs), VsArguments(f)))
            return "vsDiffMerge";
        if (FindTortoiseMerge() is { } tm && Start(new ProcessStartInfo(tm) { Arguments = TortoiseArguments(f) }, null))
            return "TortoiseMerge";
        return null;
    }

    private static bool Start(ProcessStartInfo psi, IReadOnlyList<string>? args)
    {
        try
        {
            psi.UseShellExecute = false;
            foreach (var a in args ?? [])
                psi.ArgumentList.Add(a);
            return Process.Start(psi) is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            BackendLog.Warn($"Editor di merge non avviato ({Path.GetFileName(psi.FileName)}): {ex.Message}");
            return false;
        }
    }
}
