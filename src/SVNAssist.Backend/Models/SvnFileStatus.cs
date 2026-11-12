namespace SVNAssist.Models;

/// <summary>
/// Rappresenta lo stato di un singolo file nella working copy SVN.
/// Corrisponde a un elemento <c>&lt;entry&gt;</c> nell'output di <c>svn status --xml</c>.
/// </summary>
/// <param name="Path">Percorso del file relativo alla root della working copy.</param>
/// <param name="Status">Tipo di modifica rilevata da SVN (Modified, Added, ecc.).</param>
/// <param name="IsTreeConflict">
/// <c>true</c> se il conflitto è "ad albero" (rinomina/cancellazione in conflitto): non ha
/// marcatori nel file e non si risolve con <c>--accept working</c> come un conflitto di testo.
/// </param>
/// <param name="IsLocked">
/// <c>true</c> se la working copy tiene un lock (<c>svn lock</c>) su questo file (elemento <c>&lt;lock&gt;</c> in
/// <c>wc-status</c>, "K" di svn): è un dato locale, non serve la rete. I lock di altri richiedono <c>status -u</c> e non sono letti.
/// </param>
public sealed record SvnFileStatus(string Path, SvnStatusKind Status, bool IsTreeConflict = false, bool IsLocked = false);

/// <summary>
/// Enumerazione degli stati possibili di un file in SVN.
/// I valori corrispondono all'attributo <c>item</c> nell'XML di <c>svn status</c>.
/// </summary>
public enum SvnStatusKind
{
    /// <summary>Nessuna modifica.</summary>
    Normal,

    /// <summary>File aggiunto (schedulato per il prossimo commit).</summary>
    Added,

    /// <summary>File modificato rispetto alla revisione base.</summary>
    Modified,

    /// <summary>File eliminato (schedulato per la rimozione).</summary>
    Deleted,

    /// <summary>File in conflitto dopo un merge o update.</summary>
    Conflicted,

    /// <summary>File presente su disco ma non versionato da SVN.</summary>
    Unversioned,

    /// <summary>File versionato ma mancante dal disco.</summary>
    Missing,

    /// <summary>File sostituito (delete + add nella stessa operazione).</summary>
    Replaced,

    /// <summary>File ignorato (svn:ignore / global-ignores). Non compare tra le modifiche.</summary>
    Ignored,

    /// <summary>Cartella creata da una definizione <c>svn:externals</c>. Non compare tra le modifiche.</summary>
    External,

    /// <summary>Un elemento di tipo diverso (es. file al posto di una cartella) occupa il percorso versionato.</summary>
    Obstructed,

    /// <summary>Cartella con contenuto incompleto (checkout/update interrotto): serve Cleanup/Update.</summary>
    Incomplete,

    /// <summary>Stato non riconosciuto — fallback di sicurezza.</summary>
    Unknown
}
