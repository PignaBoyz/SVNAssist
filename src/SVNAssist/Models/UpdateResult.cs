namespace SVNAssist.Models;

/// <summary>
/// Esito di <c>svn update</c>.
/// </summary>
/// <param name="Revision">Revisione a cui la working copy è arrivata (null se non riconosciuta nell'output).</param>
/// <param name="ConflictedPaths">Percorsi (come stampati da svn) rimasti in conflitto dopo l'update.</param>
public sealed record UpdateResult(long? Revision, IReadOnlyList<string> ConflictedPaths);
