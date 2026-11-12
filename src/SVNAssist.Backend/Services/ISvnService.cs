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
    /// <param name="paths">
    /// Percorsi da committare, esattamente quelli (come TortoiseSVN): le cartelle si committano a
    /// <c>--depth empty</c>, senza trascinarsi dietro i figli non elencati.
    /// </param>
    /// <param name="message">Messaggio di commit.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    /// <param name="includeAllUnder">
    /// Cartelle appena aggiunte per intero (es. una cartella non versionata spuntata): il commit include anche
    /// tutto ciò che hanno dentro.
    /// </param>
    /// <returns>Il numero della nuova revisione (<c>null</c> se non c'era nulla da committare).</returns>
    /// <exception cref="InvalidOperationException">
    /// Se il commit fallisce (es. file non aggiornato: il messaggio suggerisce di fare Update).
    /// </exception>
    Task<long?> CommitAsync(
        IEnumerable<string> paths, string message, CancellationToken ct, IEnumerable<string>? includeAllUnder = null);

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
    /// <param name="force">
    /// <c>true</c> (<c>--force</c>) per file con modifiche locali o aggiunti non ancora committati, che svn altrimenti
    /// rifiuta (E195006): da usare solo dopo la conferma dell'utente.
    /// </param>
    Task DeleteAsync(IEnumerable<string> paths, CancellationToken ct, bool force = false);

    /// <summary>
    /// Annulla le modifiche locali su file/cartelle (<c>svn revert</c>).
    /// </summary>
    /// <param name="paths">Percorsi completi su cui annullare le modifiche.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    /// <param name="recursive">
    /// <c>true</c> (<c>--depth infinity</c>) annulla anche tutto il sottoalbero; <c>false</c> (<c>--depth empty</c>)
    /// solo gli elementi elencati (una cartella con modifiche alle sole proprietà non tocca i figli).
    /// Una cartella aggiunta si può annullare solo ricorsivamente (svn E155038).
    /// </param>
    Task RevertAsync(IEnumerable<string> paths, CancellationToken ct, bool recursive = true);

    /// <summary>
    /// Marca un conflitto come risolto (<c>svn resolve --accept</c>).
    /// </summary>
    /// <param name="path">Percorso completo del file in conflitto.</param>
    /// <param name="accept">Quale versione tenere.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    /// <exception cref="InvalidOperationException">
    /// Con <see cref="SvnConflictChoice.Working"/> se il file contiene ancora i marcatori di conflitto.
    /// </exception>
    Task ResolveAsync(string path, SvnConflictChoice accept, CancellationToken ct);

    /// <summary>
    /// Aggiunge il nome file/cartella alla proprietà <c>svn:ignore</c> della directory padre.
    /// </summary>
    /// <param name="path">Percorso completo da ignorare.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    Task AddToIgnoreListAsync(string path, CancellationToken ct);

    /// <summary>
    /// Aggiorna la working copy all'ultima revisione dal repository.
    /// </summary>
    /// <param name="workingCopyPath">Percorso root della working copy SVN, o di una cartella/un file da aggiornare da soli.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    /// <returns>Revisione raggiunta ed eventuali file in conflitto.</returns>
    Task<UpdateResult> UpdateAsync(string workingCopyPath, CancellationToken ct);

    /// <summary>
    /// Aggiorna a una revisione precisa (<c>svn update -r N</c>).
    /// </summary>
    /// <param name="workingCopyPath">Root della working copy, una cartella o un file.</param>
    /// <param name="revision">Intero positivo o <c>HEAD</c> (vedi <see cref="SvnRevision.TryNormalize"/>).</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    /// <exception cref="ArgumentException">Se la revisione non è valida.</exception>
    Task<UpdateResult> UpdateToRevisionAsync(string workingCopyPath, string revision, CancellationToken ct);

    /// <summary>Ripulisce la working copy da un'operazione interrotta (<c>svn cleanup</c>); su un file agisce sulla sua cartella.</summary>
    Task CleanupAsync(string path, CancellationToken ct);

    /// <summary>Blocca i file (<c>svn lock</c>) con un messaggio facoltativo; <paramref name="force"/> = <c>--force</c> ruba il lock di un altro.</summary>
    Task LockAsync(IEnumerable<string> paths, string? message, bool force, CancellationToken ct);

    /// <summary>Sblocca i file bloccati da questa working copy (<c>svn unlock</c>).</summary>
    Task UnlockAsync(IEnumerable<string> paths, CancellationToken ct);

    /// <summary>
    /// Restituisce le ultime N voci del log SVN per il percorso dato.
    /// </summary>
    /// <param name="targetPath">Percorso o URL del target SVN.</param>
    /// <param name="maxEntries">Numero massimo di revisioni da restituire.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    Task<IReadOnlyList<SvnLogEntry>> GetLogAsync(string targetPath, int maxEntries, CancellationToken ct);

    /// <summary>Come <see cref="GetLogAsync(string,int,CancellationToken)"/> ma con filtri (autore, testo, percorso, date).</summary>
    Task<IReadOnlyList<SvnLogEntry>> GetLogAsync(string targetPath, int maxEntries, SvnLogFilter filter, CancellationToken ct);

    /// <summary>URL della radice del repository di un percorso o URL (<c>svn info --show-item repos-root-url</c>).</summary>
    Task<string> GetRepositoryRootAsync(string target, CancellationToken ct);

    /// <summary>Esporta un file alla revisione indicata (<c>svn export -r REV target@REV</c>); <paramref name="target"/> e' un URL o un percorso locale.</summary>
    Task ExportRevisionAsync(string target, long revision, string destination, CancellationToken ct);

    /// <summary>File cambiati tra due revisioni (<c>svn diff --summarize --xml -r A:B</c>).</summary>
    Task<IReadOnlyList<SvnChange>> GetChangedBetweenAsync(string target, long fromRevision, long toRevision, CancellationToken ct);

    /// <summary>
    /// Restituisce il diff testuale per una specifica revisione.
    /// </summary>
    /// <param name="targetPath">Percorso o URL del target SVN.</param>
    /// <param name="revision">Numero di revisione di cui ottenere il diff.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    Task<string> GetDiffAsync(string targetPath, long revision, CancellationToken ct);

    /// <summary>
    /// Restituisce il diff <b>minimo</b> dei soli file indicati, pensato per l'AI (commit message):
    /// niente righe di contesto, intestazione di funzione nelle hunk, file cancellati senza contenuto,
    /// file nuovi non versionati come diff sintetico (anteprima limitata), binari omessi.
    /// </summary>
    /// <param name="paths">Percorsi completi dei file selezionati.</param>
    /// <param name="workingCopyPath">Root della working copy: se indicata i percorsi nel diff sono relativi ad essa.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    Task<string> GetDiffForAiAsync(IReadOnlyList<string> paths, string? workingCopyPath, CancellationToken ct);

    /// <summary>
    /// Restituisce il diff delle modifiche locali non committate per un singolo file.
    /// </summary>
    /// <param name="filePath">Percorso completo del file.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    Task<string> GetFileDiffAsync(string filePath, CancellationToken ct);

    /// <summary>
    /// Esporta la versione BASE (pristine, senza modifiche locali) di un file della working copy in <paramref name="destination"/>
    /// (<c>svn export -r BASE</c>): serve all'host per aprire il diff nell'editor. Fallisce per i file senza BASE (aggiunti).
    /// </summary>
    Task ExportBaseAsync(string path, string destination, CancellationToken ct);

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

    /// <summary><c>svn info --xml</c> sulla radice della working copy: URL, URL relativo, radice del repository, revisioni.</summary>
    Task<SvnInfo> GetInfoAsync(string workingCopyPath, CancellationToken ct);

    /// <summary>Versione di svn (<c>svn --version --quiet</c>); <c>null</c> se non si riesce a leggerla. Le funzioni che richiedono svn &lt; 1.8 si nascondono.</summary>
    Task<Version?> GetSvnVersionAsync(CancellationToken ct);

    /// <summary>
    /// Cartelle direttamente sotto un URL con l'ultima modifica (<c>svn list --xml</c>), per l'elenco di branch/tag.
    /// Un percorso che non esiste (es. repository senza <c>tags</c>) non e' un errore: elenco vuoto.
    /// </summary>
    Task<IReadOnlyList<SvnListEntry>> ListEntriesAsync(string url, CancellationToken ct);

    /// <summary>
    /// Dati dei conflitti di testo/proprieta' dei file indicati (<c>svn info --xml</c>): tipo e file di appoggio.
    /// La chiave e' il percorso completo (senza distinzione tra maiuscole e minuscole). Un file senza dati non compare; se svn fallisce, elenco vuoto.
    /// </summary>
    Task<IReadOnlyDictionary<string, SvnConflictInfo>> GetConflictInfoAsync(IReadOnlyList<string> paths, CancellationToken ct);

    /// <summary>Porta la working copy su un altro URL (<c>svn switch --accept postpone</c>), dalla sua radice. I conflitti restano da risolvere.</summary>
    Task<UpdateResult> SwitchAsync(string workingCopyPath, string url, CancellationToken ct);

    /// <summary>Revisioni del sorgente ancora da unire nella working copy (<c>svn mergeinfo --show-revs eligible SRC .</c>), in ordine crescente. Richiede svn 1.5+ (l'unione automatica 1.8+).</summary>
    Task<IReadOnlyList<long>> GetEligibleRevisionsAsync(string workingCopyPath, string sourceUrl, CancellationToken ct);

    /// <summary>Autore, data e messaggio delle revisioni del sorgente comprese tra <paramref name="min"/> e <paramref name="max"/> (<c>svn log --xml -v -r max:min URL</c>), dalla piu' recente.</summary>
    Task<IReadOnlyList<SvnLogEntry>> GetLogRangeAsync(string url, long min, long max, CancellationToken ct);

    /// <summary>
    /// <c>svn merge [--dry-run] --accept postpone [-c N,M] SRC</c> dalla radice della working copy. Senza <paramref name="revisions"/> unisce tutto quello che e' da unire
    /// (merge automatico, svn 1.8+); con le revisioni fa il cherry-pick. Non committa.
    /// </summary>
    Task<MergeOutput> MergeAsync(string workingCopyPath, string sourceUrl, IReadOnlyList<long>? revisions, bool dryRun, CancellationToken ct);
}
