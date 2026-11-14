namespace SVNAssist.Models;

/// <summary>
/// Opzioni del Cleanup come in TortoiseSVN. <c>svn cleanup</c> sblocca sempre i lock di scrittura e ripara i time stamp insieme al
/// ripristino dello stato: da riga di comando non si separano, quindi la UI li mostra legati al cleanup.
/// </summary>
/// <param name="Status">"Clean up working copy status" (predefinito).</param>
public sealed record CleanupOptions(
    bool Status = true, bool VacuumPristines = false, bool IncludeExternals = false,
    bool RemoveUnversioned = false, bool RemoveIgnored = false, bool RevertAll = false)
{
    public static CleanupOptions Default { get; } = new();

    /// <summary>Opzioni che cancellano lavoro non recuperabile: richiedono conferma esplicita.</summary>
    public bool Destructive => RemoveUnversioned || RemoveIgnored || RevertAll;

    /// <summary>Serve <c>svn cleanup</c> (le altre opzioni lo richiedono comunque).</summary>
    public bool RunsCleanup => Status || UsesExtras;

    /// <summary>Opzioni oltre il cleanup semplice: esistono da svn 1.9.</summary>
    public bool UsesExtras => VacuumPristines || IncludeExternals || RemoveUnversioned || RemoveIgnored;

    /// <summary><c>--vacuum-pristines</c>, <c>--include-externals</c>, l'eliminazione di non versionati/ignorati richiedono svn 1.9 (versione sconosciuta = si prova).</summary>
    public static bool ExtrasSupported(Version? svn) => svn is null || svn >= new Version(1, 9);

    /// <summary>Argomenti di <c>svn cleanup</c>: <c>--remove-*</c> non si passano, i non versionati/ignorati li elimina SVNAssist (VS tiene file aperti) (<c>--non-interactive</c> lo aggiunge SvnService).</summary>
    public List<string> ToArgs()
    {
        var args = new List<string> { "cleanup" };
        if (VacuumPristines) args.Add("--vacuum-pristines");
        if (IncludeExternals) args.Add("--include-externals");
        return args;
    }
}
