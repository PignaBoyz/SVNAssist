namespace SVNAssist.Models;

/// <summary>
/// Opzioni per un'operazione di commit SVN.
/// Raggruppa i parametri necessari per eseguire un commit in un unico oggetto,
/// rendendo più facile estendere le opzioni in futuro senza cambiare le firme dei metodi.
/// </summary>
/// <param name="Paths">Percorsi dei file da includere nel commit.</param>
/// <param name="Message">Messaggio di commit.</param>
public sealed record CommitOptions(
    IReadOnlyList<string> Paths,
    string Message);
