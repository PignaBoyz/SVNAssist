using SVNAssist.Models;

namespace SVNAssist.Services;

/// <summary>
/// Dialoghi modali usati dal ViewModel per conferme e scelte dell'utente.
/// </summary>
/// <remarks>
/// Dietro interfaccia come <see cref="IAiSetupPrompt"/>: il ViewModel resta unit-testabile
/// (si inietta un dialogo finto) e l'implementazione con l'API Shell di Visual Studio
/// vive fuori dalla logica.
/// </remarks>
internal interface IDialogService
{
    /// <summary>Chiede conferma (OK / Annulla). <c>false</c> se l'utente annulla o chiude il dialogo.</summary>
    /// <param name="message">Testo del dialogo (può contenere più righe).</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    Task<bool> ConfirmAsync(string message, CancellationToken ct);

    /// <summary>
    /// Chiede come risolvere un conflitto di testo (Working / Mine-full / Theirs-full / Base).
    /// </summary>
    /// <param name="fileName">Nome del file in conflitto, mostrato nel messaggio.</param>
    /// <param name="ct">Token per annullare l'operazione.</param>
    /// <returns>La scelta, oppure <c>null</c> se l'utente annulla.</returns>
    Task<SvnConflictChoice?> ChooseConflictResolutionAsync(string fileName, CancellationToken ct);
}
