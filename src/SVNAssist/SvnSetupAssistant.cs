using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;
using SVNAssist.Services;

namespace SVNAssist;

/// <summary>
/// Orchestrazione UI per la verifica della presenza di SVN: se <c>svn.exe</c> manca,
/// mostra un prompt che propone all'utente di installarlo.
/// </summary>
/// <remarks>
/// Separata da <see cref="SvnEnvironment"/> (che è pura logica senza dipendenze VS) perché
/// questa classe usa l'API <c>Shell()</c> di Visual Studio per mostrare il prompt.
/// Il controllo viene eseguito al massimo una volta per sessione per non infastidire l'utente.
/// </remarks>
internal static class SvnSetupAssistant
{
    private static int _alreadyPrompted;

    /// <summary>
    /// Verifica che <c>svn.exe</c> sia disponibile. Se manca, propone l'installazione
    /// (una sola volta per sessione).
    /// </summary>
    /// <returns><c>true</c> se SVN è disponibile; <c>false</c> se manca.</returns>
    public static async Task<bool> EnsureSvnAvailableAsync(VisualStudioExtensibility extensibility, CancellationToken ct)
    {
        if (SvnEnvironment.IsSvnAvailable)
            return true;

        // Interlocked: mostriamo il prompt una sola volta anche se più tool window/comandi
        // chiamano questo metodo in parallelo.
        if (Interlocked.Exchange(ref _alreadyPrompted, 1) == 1)
            return false;

        var installNow = await extensibility.Shell().ShowPromptAsync(
            "SVNAssist non ha trovato il client a riga di comando di Subversion (svn.exe) sul sistema.\n\n" +
            "È necessario per tutte le operazioni SVN. Vuoi installarlo ora?",
            PromptOptions.OKCancel,
            ct);

        if (!installNow)
            return false;

        // Prova prima winget (installazione automatica); se non disponibile, apre la pagina di download.
        if (!SvnEnvironment.TryStartWingetInstall())
            SvnEnvironment.OpenDownloadPage();

        await extensibility.Shell().ShowPromptAsync(
            "Una volta completata l'installazione di SVN, riavvia Visual Studio " +
            "(o riapri il pannello SVN) per usare SVNAssist.",
            PromptOptions.OK,
            ct);

        return false;
    }
}
