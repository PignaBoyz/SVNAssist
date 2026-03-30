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
    /// Aggiunge file/cartelle al versionamento SVN (<c>svn add</c>).
    /// </summary>
    /// <param name="paths">Percorsi completi da aggiungere.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    Task AddAsync(IEnumerable<string> paths, CancellationToken ct);

    /// <summary>
    /// Schedula la rimozione di file/cartelle versionati (<c>svn delete</c>).
    /// </summary>
    /// <param name="paths">Percorsi completi da rimuovere.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    Task DeleteAsync(IEnumerable<string> paths, CancellationToken ct);

    /// <summary>
    /// Annulla le modifiche locali su file/cartelle (<c>svn revert</c>).
    /// </summary>
    /// <param name="paths">Percorsi completi su cui annullare le modifiche.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    Task RevertAsync(IEnumerable<string> paths, CancellationToken ct);

    /// <summary>
    /// Marca un conflitto come risolto mantenendo il contenuto locale.
    /// </summary>
    /// <param name="path">Percorso completo del file in conflitto.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    Task ResolveAsync(string path, CancellationToken ct);

    /// <summary>
    /// Aggiunge il nome file/cartella alla proprietà <c>svn:ignore</c> della directory padre.
    /// </summary>
    /// <param name="path">Percorso completo da ignorare.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    Task AddToIgnoreListAsync(string path, CancellationToken ct);

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

    /// <summary>
    /// Restituisce il diff delle modifiche locali non committate per un singolo file.
    /// </summary>
    /// <param name="filePath">Percorso completo del file.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    Task<string> GetFileDiffAsync(string filePath, CancellationToken ct);

    /// <summary>
    /// Apre la finestra diff del client SVN nativo (es. TortoiseSVN) per il file indicato.
    /// </summary>
    /// <param name="filePath">Percorso completo del file da confrontare.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    /// <returns><c>true</c> se la finestra è stata aperta; <c>false</c> se non è disponibile un client nativo.</returns>
    Task<bool> OpenNativeDiffAsync(string filePath, CancellationToken ct);

    /// <summary>
    /// Restituisce il nome del branch SVN corrente nella working copy.
    /// Usa <c>svn info</c> per estrarre l'URL relativo e derivare il branch name.
    /// </summary>
    /// <param name="workingCopyPath">Percorso root della working copy SVN.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    /// <returns>Il nome del branch (es. "trunk", "branches/feature-x").</returns>
    Task<string> GetBranchNameAsync(string workingCopyPath, CancellationToken ct);
}
