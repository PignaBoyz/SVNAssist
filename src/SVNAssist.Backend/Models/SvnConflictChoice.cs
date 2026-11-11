namespace SVNAssist.Models;

/// <summary>
/// Come risolvere un conflitto (corrisponde a <c>svn resolve --accept</c>).
/// </summary>
public enum SvnConflictChoice
{
    /// <summary>Tieni il file così com'è (<c>working</c>): dopo averlo sistemato a mano. Rifiutato se restano marcatori.</summary>
    Working,

    /// <summary>Tieni tutto il file locale, scartando le modifiche del server (<c>mine-full</c>).</summary>
    MineFull,

    /// <summary>Tieni tutto il file del server, scartando le modifiche locali (<c>theirs-full</c>).</summary>
    TheirsFull,

    /// <summary>Torna alla versione base, prima delle modifiche di entrambi (<c>base</c>).</summary>
    Base,
}
