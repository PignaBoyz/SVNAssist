using SVNAssist.Models;

namespace SVNAssist.Services;

/// <summary>
/// Interfaccia per le operazioni SVN.
/// Astrae l'accesso a Subversion — l'implementazione concreta (<see cref="SvnService"/>)
/// usa <c>svn.exe</c> via CLI, ma grazie all'interfaccia i test possono usare un mock
/// e in futuro si potrebbe sostituire con un'altra libreria.
/// </summary>
public interface ISvnService
{
    /// <summary>
    /// Restituisce lo stato di tutti i file nella working copy (modificati, aggiunti, ecc.).
    /// </summary>
    /// <param name="workingCopyPath">Percorso root della working copy SVN.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    Task<IReadOnlyList<SvnFileStatus>> GetStatusAsync(string workingCopyPath, CancellationToken ct);

    /// <summary>
    /// Esegue il commit dei file specificati con il messaggio dato.
    /// </summary>
    /// <param name="paths">Percorsi dei file da committare.</param>
    /// <param name="message">Messaggio di commit.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    Task CommitAsync(IEnumerable<string> paths, string message, CancellationToken ct);

    /// <summary>
    /// Aggiorna la working copy all'ultima revisione dal repository.
    /// </summary>
    /// <param name="workingCopyPath">Percorso root della working copy SVN.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    Task UpdateAsync(string workingCopyPath, CancellationToken ct);

    /// <summary>
    /// Restituisce le ultime N voci del log SVN per il percorso dato.
    /// </summary>
    /// <param name="targetPath">Percorso o URL del target SVN.</param>
    /// <param name="maxEntries">Numero massimo di revisioni da restituire.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    Task<IReadOnlyList<SvnLogEntry>> GetLogAsync(string targetPath, int maxEntries, CancellationToken ct);

    /// <summary>
    /// Restituisce il diff testuale per una specifica revisione.
    /// </summary>
    /// <param name="targetPath">Percorso o URL del target SVN.</param>
    /// <param name="revision">Numero di revisione di cui ottenere il diff.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    Task<string> GetDiffAsync(string targetPath, long revision, CancellationToken ct);

    /// <summary>
    /// Restituisce il diff delle modifiche non committate nella working copy.
    /// Usato dall'AI per analizzare le modifiche e generare il commit message.
    /// </summary>
    /// <param name="workingCopyPath">Percorso root della working copy SVN.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    Task<string> GetWorkingCopyDiffAsync(string workingCopyPath, CancellationToken ct);
}
