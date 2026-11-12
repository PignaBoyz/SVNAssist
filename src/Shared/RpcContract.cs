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
        /// command: update | add | revert | diff | ignore | log | commit. L'host apre prima la finestra
        /// (Changes per commit, Log per log); l'esito si mostra con IHost.PromptAsync / toast.
        /// </summary>
        Task RunPathCommandAsync(string command, string[] paths);
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

        /// <summary>Mostra la finestra SVN Log con l'elenco dei file modificati di una cartella (clic = diff nativo, "Apri come patch" = .diff unico).</summary>
        Task ShowFolderDiffAsync(string path);

        /// <summary>Apre un file nell'editor.</summary>
        Task OpenFileAsync(string path);

        /// <summary>Mostra la finestra SVN Log su un percorso.</summary>
        Task ShowLogAsync(string path);

        /// <summary>Apre Tools > Options > SVNAssist ("Faccio io").</summary>
        Task OpenSettingsAsync();

        /// <summary>Apre un terminale con un comando (es. "gh auth login --web").</summary>
        Task RunInTerminalAsync(string command, string workingDir);
    }
}
