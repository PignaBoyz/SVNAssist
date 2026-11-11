using System.Diagnostics;

namespace SVNAssist.Services;

/// <summary>
/// Verifica la presenza del client a riga di comando di Subversion sulla macchina
/// e aiuta l'utente a installarlo se manca.
/// </summary>
/// <remarks>
/// Il modello VisualStudio.Extensibility (out-of-process) non offre un hook di "custom action"
/// durante l'installazione del VSIX come faceva un installer MSI. La cosa più vicina è una
/// <b>verifica al primo utilizzo</b>: quando l'utente apre il pannello SVN o lancia un comando,
/// controlliamo che <c>svn.exe</c> esista e, se manca, proponiamo di installarlo.
/// Così si evitano errori criptici "svn non trovato" durante le operazioni.
/// </remarks>
public static class SvnEnvironment
{
    /// <summary>Pagina di download del client SVN (SlikSVN, CLI-only).</summary>
    public const string DownloadUrl = "https://sliksvn.com/download/";

    /// <summary>ID winget del pacchetto SlikSVN (client a riga di comando).</summary>
    private const string WingetPackageId = "Slik.Subversion";

    /// <summary><c>true</c> se <c>svn.exe</c> è disponibile sulla macchina.</summary>
    public static bool IsSvnAvailable => SvnLocator.IsSvnAvailable;

    /// <summary>
    /// Avvia l'installazione di SVN tramite winget in una finestra visibile.
    /// </summary>
    /// <returns><c>true</c> se winget è stato avviato; <c>false</c> se non disponibile.</returns>
    public static bool TryStartWingetInstall()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = $"install --id {WingetPackageId} -e " +
                            "--accept-package-agreements --accept-source-agreements",
                // UseShellExecute = true: risolve l'alias di winget e mostra una finestra
                // con l'avanzamento (ed eventuale prompt UAC) all'utente.
                UseShellExecute = true,
            };
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Apre la pagina di download del client SVN nel browser predefinito.
    /// Fallback quando winget non è disponibile.
    /// </summary>
    public static void OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = DownloadUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort: se non riusciamo ad aprire il browser, non blocchiamo l'estensione.
        }
    }
}
