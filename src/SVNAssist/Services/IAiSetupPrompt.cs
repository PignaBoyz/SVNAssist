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
    /// Mostrato al massimo una volta per sessione.
    /// </summary>
    Task ShowMissingTokenHelpAsync(CancellationToken ct);
}
