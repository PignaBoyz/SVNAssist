namespace SVNAssist.Models;

/// <summary>Un percorso toccato da una revisione (<c>svn log -v</c>).</summary>
/// <param name="Action">Azione svn: A (aggiunto), M (modificato), D (eliminato), R (sostituito).</param>
/// <param name="Path">Percorso nel repository (es. <c>/trunk/Core/A.cs</c>).</param>
public sealed record SvnLogPath(string Action, string Path);

/// <summary>
/// Rappresenta una singola voce nel log SVN (una revisione).
/// Corrisponde a un elemento <c>&lt;logentry&gt;</c> nell'output di <c>svn log --xml</c>.
/// </summary>
/// <param name="Revision">Numero di revisione SVN.</param>
/// <param name="Author">Nome dell'autore del commit.</param>
/// <param name="Date">Data e ora del commit.</param>
/// <param name="Message">Messaggio di commit.</param>
/// <param name="Paths">Percorsi toccati (solo con <c>-v</c>); vuoto se non richiesti.</param>
public sealed record SvnLogEntry(
    long Revision,
    string Author,
    DateTimeOffset Date,
    string Message,
    IReadOnlyList<SvnLogPath>? Paths = null);
