// Contratto RPC host (net472, in devenv) <-> backend (net8, SVNAssist.Backend.exe), StreamJsonRpc su stdio.
// File condiviso: incluso come <Compile Include="..\Shared\RpcContract.cs" Link="RpcContract.cs" /> da entrambi i progetti.
// I payload UI sono le stringhe JSON del protocollo docs/ui-protocol.md: l'host fa solo relay, non le interpreta.
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SVNAssist.Rpc
{
    /// <summary>Metodi esposti dal BACKEND, chiamati dall'host.</summary>
    public interface IBackend
    {
        /// <summary>Messaggio JS→host (ready/cmd) inoltrato così com'è. view = "changes" | "log" | "diff".</summary>
        Task DispatchAsync(string view, string messageJson);

        /// <summary>Impostazioni cambiate (Tools > Options): il backend rilegge con IHost.GetSettingsAsync.</summary>
        Task SettingsChangedAsync();

        /// <summary>Solution aperta/chiusa: cartella della solution, null se nessuna.</summary>
        Task SolutionChangedAsync(string? solutionDir);

        /// <summary>
        /// Tema di Visual Studio corrente: "light" | "dark" | "blue". Va chiamato all'avvio e a ogni cambio tema.
        /// Serve a riempire <c>theme.kind</c> nello snapshot quando l'utente ha scelto il modo "auto"
        /// (il backend non vede i colori di VS; i messaggi <c>theme</c> con le variabili CSS li manda l'host).
        /// </summary>
        Task ThemeChangedAsync(string kind);

        /// <summary>
        /// Comando del menu contestuale di Solution Explorer sui percorsi selezionati.
        /// command: update | add | revert | diff | ignore | log | commit | cleanup | updaterev | lock | unlock. L'host apre prima la finestra
        /// (Changes per commit, Log per log); l'esito si mostra con IHost.PromptAsync / toast.
        /// </summary>
        Task RunPathCommandAsync(string command, string[] paths);

        /// <summary>Righe iniziali (1-based, nel file di lavoro) dei blocchi modificati rispetto a BASE, per "prossima/precedente modifica" nell'editor. Vuoto se il file non e' versionato o non e' modificato.</summary>
        Task<int[]> GetChangedBlocksAsync(string file);

        /// <summary>
        /// "Chi ha toccato la riga" (blame acceso nell'editor): JSON <c>{"revs":[{"r","a","d"}],"lines":[indice in revs | -1]}</c>,
        /// lines[i] = riga i+1 del file su disco (-1 = modifica locale non committata), oppure <c>{"error":"..."}</c>.
        /// Il backend tiene il blame in cache finche' la revisione del file non cambia.
        /// </summary>
        Task<string> GetBlameAsync(string file);

        /// <summary>Prima riga del messaggio di una revisione del file (per il testo del blame), "" se non disponibile.</summary>
        Task<string> GetBlameMessageAsync(string file, long revision);

        /// <summary>Visual Studio e' diventata l'applicazione attiva (true) o ha perso il primo piano (false): il controllo delle nuove revisioni si ferma in secondo piano.</summary>
        Task IdeActiveChangedAsync(bool active);

        /// <summary>Stato SVN dei percorsi non "normali" della working copy aperta (percorso completo -> nome di SvnStatusKind), dalla cache in memoria di Changes: per i glifi di Solution Explorer.</summary>
        Task<Dictionary<string, string>> GetSccStatesAsync();

        /// <summary>
        /// Modelli AI realmente disponibili per un provider (copilot | claudecode | claude | openai | gemini), come JSON
        /// <c>{provider, source: live|cache|fallback, updated, account, error, models:[{id,label,cost}]}</c>. Cache datata di 24 ore:
        /// forceRefresh la ignora. La UI web non lo usa (passa dai comandi wizard*): serve all'host, ad esempio per le Options.
        /// </summary>
        Task<string> ListAiModelsAsync(string provider, bool forceRefresh);

        /// <summary>Metto in pausa o riprendo il refresh automatico del file system (es. durante Build o caricamento solution in VS).</summary>
        Task SetAutoRefreshPausedAsync(bool paused);

        /// <summary>Notifica dal salvataggio documento in VS: triggera un refresh debounced anche in modalità leggera.</summary>
        Task OnDocumentSavedAsync(string filePath);

        /// <summary>L'editor di merge in-process di VS per <paramref name="filePath"/> è stato chiuso: verifica se il conflitto è stato risolto.</summary>
        Task OnMergeEditorClosedAsync(string filePath, int state);
    }

    /// <summary>Metodi esposti dall'HOST, chiamati dal backend.</summary>
    public interface IHost
    {
        /// <summary>Messaggio host→JS (state/reply/event) da consegnare alla WebView della vista.</summary>
        Task PushAsync(string view, string messageJson);

        /// <summary>Dialogo nativo VS. Ritorna l'indice del pulsante scelto, -1 se annullato.</summary>
        Task<int> PromptAsync(string message, string[] buttons);

        /// <summary>Impostazioni correnti, chiavi come in SettingsService (es. "aiProvider", "claudeModel").</summary>
        Task<Dictionary<string, string>> GetSettingsAsync();

        /// <summary>Salva un'impostazione (es. "windowTheme" da setTheme).</summary>
        Task SetSettingAsync(string key, string value);

        /// <summary>Apre un URL https nel browser predefinito (l'host rifiuta altri schemi).</summary>
        Task OpenUrlAsync(string url);

        /// <summary>Apre il diff nell'editor di VS (IVsDifferenceService), come Git Changes.</summary>
        Task OpenDiffAsync(string leftPath, string rightPath, string leftLabel, string rightLabel, string caption);

        /// <summary>
        /// Apre l'editor di merge a 3 vie nativo dentro QUESTA istanza di Visual Studio (<c>SVsFileMergeService</c>).
        /// Ritorna <c>true</c> se aperto in-process; <c>false</c> se non disponibile (fallback esterno).
        /// </summary>
        Task<bool> OpenMergeAsync(string leftPath, string rightPath, string basePath, string resultPath, string leftLabel, string rightLabel, string baseLabel, string resultLabel);

        /// <summary>Mostra la finestra SVN Log con l'elenco dei file modificati di una cartella (clic = diff nativo, "Apri come patch" = .diff unico).</summary>
        Task ShowFolderDiffAsync(string path);

        /// <summary>Apre un file nell'editor.</summary>
        Task OpenFileAsync(string path);

        /// <summary>Apre il Log come documento nell'area editor (titolo "Log: nome") su un percorso.</summary>
        Task ShowLogAsync(string path);

        /// <summary>
        /// Apre il Log come documento con il titolo dato ("Merge da branches/x", "Log: nome") senza caricare nulla:
        /// il contenuto (merge o log) lo ha gia' impostato il backend.
        /// </summary>
        Task ShowLogWindowAsync(string title);

        /// <summary>Porta in primo piano la finestra SVN Changes (es. dopo un merge con conflitti da risolvere).</summary>
        Task ShowChangesWindowAsync();

        /// <summary>Apre Tools > Options > SVNAssist ("Faccio io").</summary>
        Task OpenSettingsAsync();

        /// <summary>
        /// Dialogo con un campo di testo (revisione, messaggio di lock). Ritorna il testo digitato, null se annullato.
        /// </summary>
        Task<string?> PromptTextAsync(string message, string label, string defaultValue);

        /// <summary>
        /// Dialogo utente/password (password mascherata, "Ricorda" di default attivo), mostrato SOLO dopo un errore di
        /// autenticazione di svn. Ritorna [utente, password, "1"|"0" (ricorda)], null se annullato. Non va mai loggato.
        /// </summary>
        Task<string[]?> PromptCredentialsAsync(string detail, string defaultUser);

        /// <summary>Apre un terminale con un comando (es. "gh auth login --web").</summary>
        Task RunInTerminalAsync(string command, string workingDir);

        /// <summary>
        /// Se il file e' aperto in un editor (anche il pannello destro di un diff) sostituisce il testo del buffer con una sola modifica
        /// annullabile (Ctrl+Z) e lo salva: true. File non aperto: false e non fa nulla (il backend scrive su disco).
        /// </summary>
        Task<bool> ApplyDocumentTextAsync(string path, string text);

        /// <summary>Dialogo Salva con nome (save) / Apri file per le patch. Ritorna il percorso scelto, null se annullato.</summary>
        Task<string?> PickFileAsync(bool save, string title, string defaultName, string filter);

        /// <summary>Lo stato SVN dei file e' cambiato: l'host rilegge GetSccStatesAsync e aggiorna i glifi di Solution Explorer.</summary>
        Task SccStatusChangedAsync();
    }
}
