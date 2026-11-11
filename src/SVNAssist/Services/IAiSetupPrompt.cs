namespace SVNAssist.Services;

/// <summary>
/// Mostra all'utente l'aiuto per configurare il token GitHub quando manca,
/// nel momento in cui prova a usare l'AI.
/// </summary>
/// <remarks>
/// Astrazione dietro interfaccia per due motivi: il ViewModel resta unit-testabile
/// (si inietta un prompt finto) e l'implementazione concreta, che usa l'API Shell di
/// Visual Studio, vive fuori dalla logica del ViewModel.
/// </remarks>
internal interface IAiSetupPrompt
{
    /// <summary>
    /// Propone all'utente di creare/incollare un Personal Access Token GitHub.
    /// Va mostrato a ogni richiesta senza provider pronto (mai "una volta sola": chiudere il popup non deve zittire l'estensione).
    /// Restituisce <c>true</c> se ha avviato il login o aperto la pagina, <c>false</c> se l'utente ha rinunciato.
    /// </summary>
    Task<bool> ShowMissingTokenHelpAsync(CancellationToken ct);
}
