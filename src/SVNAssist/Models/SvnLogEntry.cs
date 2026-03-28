namespace SVNAssist.Models;

/// <summary>
/// Rappresenta una singola voce nel log SVN (una revisione).
/// Corrisponde a un elemento <c>&lt;logentry&gt;</c> nell'output di <c>svn log --xml</c>.
/// </summary>
/// <param name="Revision">Numero di revisione SVN.</param>
/// <param name="Author">Nome dell'autore del commit.</param>
/// <param name="Date">Data e ora del commit.</param>
/// <param name="Message">Messaggio di commit.</param>
public sealed record SvnLogEntry(
    long Revision,
    string Author,
    DateTimeOffset Date,
    string Message);
