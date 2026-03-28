namespace SVNAssist.Models;

/// <summary>
/// Rappresenta lo stato di un singolo file nella working copy SVN.
/// Corrisponde a un elemento <c>&lt;entry&gt;</c> nell'output di <c>svn status --xml</c>.
/// </summary>
/// <param name="Path">Percorso del file relativo alla root della working copy.</param>
/// <param name="Status">Tipo di modifica rilevata da SVN (Modified, Added, ecc.).</param>
public sealed record SvnFileStatus(string Path, SvnStatusKind Status);

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

    /// <summary>Stato non riconosciuto — fallback di sicurezza.</summary>
    Unknown
}
