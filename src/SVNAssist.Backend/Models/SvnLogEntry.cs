namespace SVNAssist.Models;

/// <summary>Un percorso toccato da una revisione (<c>svn log -v</c>).</summary>
/// <param name="Action">Azione svn: A (aggiunto), M (modificato), D (eliminato), R (sostituito).</param>
/// <param name="Path">Percorso nel repository (es. <c>/trunk/Core/A.cs</c>).</param>
/// <param name="Kind"><c>file</c> o <c>dir</c> (attributo <c>kind</c> di svn log); vuoto se sconosciuto.</param>
public sealed record SvnLogPath(string Action, string Path, string Kind = "");

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

/// <summary>
/// Filtri del log (tutti facoltativi). Autore, testo e percorso: sottostringa senza distinzione maiuscole/minuscole.
/// <see cref="From"/>/<see cref="To"/>: giorni inclusi.
/// </summary>
public sealed record SvnLogFilter(string? Author = null, string? Text = null, string? Path = null, DateOnly? From = null, DateOnly? To = null)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Author) && string.IsNullOrWhiteSpace(Text)
        && string.IsNullOrWhiteSpace(Path) && From is null && To is null;

    /// <summary>Filtro in memoria (garanzia di correttezza: <c>--search</c> di svn serve solo a ridurre le righe lette).</summary>
    public bool Matches(SvnLogEntry e)
    {
        static bool Has(string hay, string? needle) =>
            string.IsNullOrWhiteSpace(needle) || hay.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase);
        var day = DateOnly.FromDateTime(e.Date.LocalDateTime);
        var path = Path?.Trim().Replace('\\', '/');
        return Has(e.Author, Author) && Has(e.Message, Text)
            && (From is not { } f || day >= f) && (To is not { } t || day <= t)
            && (string.IsNullOrWhiteSpace(path) || (e.Paths ?? []).Any(p => Has(p.Path, path)));
    }
}

/// <summary>Un file cambiato tra due revisioni o nella working copy (<c>svn diff --summarize</c>).</summary>
/// <param name="Action">A, M, D o R.</param>
/// <param name="Path">Percorso locale o URL, come lo restituisce svn.</param>
public sealed record SvnChange(string Action, string Path);
